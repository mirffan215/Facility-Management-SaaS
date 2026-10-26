using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fms.Platform.Idempotency;

namespace Fms.Api;

/// <summary>
/// Makes writes safe to retry. A client that sends <c>Idempotency-Key: &lt;unique value&gt;</c> with a POST/PUT/DELETE gets the first answer
/// back if it sends the same request again (after a dropped connection, say) and the work is not done twice. A reused key with a different
/// request is refused. Only successful answers are remembered; a failed request can simply be retried. Runs after authorisation, so only
/// people who may do the work can create keys, and keys are per person.
/// </summary>
public sealed partial class IdempotencyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "Idempotency-Key";
    private const int MaxHashedBodyBytes = 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,100}$")]
    private static partial Regex ValidKey();

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
            || !context.Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            await next(context);
            return;
        }

        var service = context.RequestServices.GetService<IdempotencyService>();
        if (service is null || context.Items[UserProvisioningMiddleware.UserIdItem] is not Guid userId)
        {
            await next(context);
            return;
        }

        var key = header.ToString().Trim();
        if (!ValidKey().IsMatch(key))
        {
            await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Idempotency-Key must be 8 to 100 letters, digits or . _ : -").ExecuteAsync(context);
            return;
        }

        var ct = context.RequestAborted;
        var hash = await HashRequestAsync(context, ct);
        if (hash is null)
        {
            await Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Request too large to be made retry-safe.").ExecuteAsync(context);
            return;
        }

        var decision = await service.BeginAsync(userId, key, method, context.Request.Path.Value ?? "", hash, ct);
        switch (decision.Outcome)
        {
            case IdempotencyOutcome.Replay:
                context.Response.StatusCode = decision.StatusCode;
                context.Response.Headers["Idempotency-Replayed"] = "true";
                if (decision.Body is { Length: > 0 })
                {
                    context.Response.ContentType = decision.ContentType;
                    await context.Response.Body.WriteAsync(decision.Body, ct);
                }

                return;
            case IdempotencyOutcome.Mismatch:
                await Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "This Idempotency-Key was already used for a different request.").ExecuteAsync(context);
                return;
            case IdempotencyOutcome.InProgress:
                context.Response.Headers.RetryAfter = "2";
                await Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The same request is still being processed. Try again in a moment.").ExecuteAsync(context);
                return;
        }

        // Proceed: run the request into a buffer so the answer can be remembered before it is sent.
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
            var status = context.Response.StatusCode;
            if (status is >= 200 and < 300) await service.CompleteAsync(decision.RecordId, status, context.Response.ContentType, buffer.ToArray(), CancellationToken.None);
            else await service.AbandonAsync(decision.RecordId, CancellationToken.None);
        }
        catch
        {
            await service.AbandonAsync(decision.RecordId, CancellationToken.None);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, CancellationToken.None);
    }

    /// <summary>Method, path and body. Uploads are identified by their declared length only (hashing a photo twice is not worth it).</summary>
    private static async Task<string?> HashRequestAsync(HttpContext context, CancellationToken ct)
    {
        var request = context.Request;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString} "));

        if (request.HasFormContentType)
        {
            sha.AppendData(Encoding.UTF8.GetBytes($"form:{request.ContentLength}"));
        }
        else if (request.ContentLength is > MaxHashedBodyBytes)
        {
            return null;
        }
        else
        {
            request.EnableBuffering(MaxHashedBodyBytes);
            var chunk = new byte[16 * 1024];
            int read;
            long total = 0;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                total += read;
                if (total > MaxHashedBodyBytes) return null;
                sha.AppendData(chunk.AsSpan(0, read));
            }

            request.Body.Position = 0;
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
