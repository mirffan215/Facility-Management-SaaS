using Fms.BuildingBlocks.Persistence;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Idempotency;

public enum IdempotencyOutcome
{
    /// <summary>First time this key is seen: do the work, then call Complete (or Abandon if it failed).</summary>
    Proceed,

    /// <summary>Seen before and finished: return the stored answer.</summary>
    Replay,

    /// <summary>Seen before with a different request: a client bug, refuse.</summary>
    Mismatch,

    /// <summary>The same request is still being processed: ask the client to try again shortly.</summary>
    InProgress,
}

public sealed record IdempotencyDecision(IdempotencyOutcome Outcome, Guid RecordId, int StatusCode = 0, string? ContentType = null, byte[]? Body = null);

public sealed class IdempotencyService(PlatformDbContext db, TimeProvider clock)
{
    /// <summary>A request that has been "in progress" longer than this is assumed to have died with its server and may be run again.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public const int MaxStoredBytes = 256 * 1024;

    public async Task<IdempotencyDecision> BeginAsync(Guid userId, string key, string method, string path, string requestHash, CancellationToken ct)
    {
        var record = new IdempotencyKey { UserId = userId, Key = key, Method = method, Path = path, RequestHash = requestHash, CreatedAt = clock.GetUtcNow() };
        db.IdempotencyKeys.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
            return new IdempotencyDecision(IdempotencyOutcome.Proceed, record.Id);
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
        }

        var existing = await db.IdempotencyKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.Key == key, ct);
        if (existing is null) return await BeginAsync(userId, key, method, path, requestHash, ct); // it was cleaned up in between: try again

        if (existing.RequestHash != requestHash || existing.Method != method || existing.Path != path) return new IdempotencyDecision(IdempotencyOutcome.Mismatch, existing.Id);
        if (existing.IsComplete) return new IdempotencyDecision(IdempotencyOutcome.Replay, existing.Id, existing.StatusCode, existing.ContentType, existing.ResponseBody);

        if (clock.GetUtcNow() - existing.CreatedAt > StaleAfter)
        {
            // Take it over, but only one caller wins (the row's old creation time must still match).
            var taken = await db.IdempotencyKeys.Where(k => k.Id == existing.Id && k.CreatedAt == existing.CreatedAt && !k.IsComplete)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.CreatedAt, clock.GetUtcNow()), ct);
            if (taken == 1) return new IdempotencyDecision(IdempotencyOutcome.Proceed, existing.Id);
        }

        return new IdempotencyDecision(IdempotencyOutcome.InProgress, existing.Id);
    }

    public async Task CompleteAsync(Guid recordId, int statusCode, string? contentType, byte[]? body, CancellationToken ct)
    {
        var stored = body is { Length: > 0 and <= MaxStoredBytes } ? body : null;
        await db.IdempotencyKeys.Where(k => k.Id == recordId).ExecuteUpdateAsync(
            s => s.SetProperty(k => k.IsComplete, true).SetProperty(k => k.StatusCode, statusCode).SetProperty(k => k.ContentType, contentType).SetProperty(k => k.ResponseBody, stored), ct);
    }

    /// <summary>The request failed (or was refused): forget the key so a retry runs again.</summary>
    public Task AbandonAsync(Guid recordId, CancellationToken ct) =>
        db.IdempotencyKeys.Where(k => k.Id == recordId && !k.IsComplete).ExecuteDeleteAsync(ct);
}
