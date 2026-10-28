using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fms.Ai.Services;

/// <summary>What the installation's operator sets (appsettings, environment variables or the key vault); nothing here is stored in the database or shown in the web app.</summary>
public sealed class AiOptions
{
    /// <summary>"None" (the default: AI is not available) or "Claude".</summary>
    public string Provider { get; set; } = "None";

    public string? ApiKey { get; set; }

    /// <summary>The address of the service. Change it to use a gateway or a private endpoint.</summary>
    public string Endpoint { get; set; } = "https://api.anthropic.com";

    public string Model { get; set; } = "claude-haiku-5-5";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxOutputTokens { get; set; } = 400;
}

public sealed record AiRequest(string System, string User, int MaxTokens);

public sealed record AiResult(bool Ok, string? Text, int InputTokens, int OutputTokens, string? Error);

/// <summary>A model behind a small interface, so a different service, a private endpoint or a fake in tests can stand in.</summary>
public interface IAiProvider
{
    string Name { get; }

    /// <summary>False when the operator has not set a provider up; the app then offers no AI at all.</summary>
    bool IsConfigured { get; }

    Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default);
}

public sealed class NoAiProvider : IAiProvider
{
    public string Name => "None";
    public bool IsConfigured => false;
    public Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default) => Task.FromResult(new AiResult(false, null, 0, 0, "AI is not set up."));
}

/// <summary>Talks to the Claude Messages API. The key is sent only in a header to the configured endpoint; the words exchanged are never logged.</summary>
public sealed class ClaudeProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly ILogger<ClaudeProvider> _log;

    public ClaudeProvider(IOptions<AiOptions> options, ILogger<ClaudeProvider> log, HttpMessageHandler? handler = null)
    {
        _options = options.Value;
        _log = log;
        _http = handler is null ? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // the call's own token carries the limit
    }

    public string Name => "Claude";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.IsLoopback);

    public async Task<AiResult> CompleteAsync(AiRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured) return new AiResult(false, null, 0, 0, "AI is not set up.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 120)));
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = _options.Model,
                max_tokens = Math.Min(request.MaxTokens, _options.MaxOutputTokens),
                system = request.System,
                messages = new[] { new { role = "user", content = request.User } },
            });
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.Endpoint), "/v1/messages")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            message.Headers.Add("x-api-key", _options.ApiKey);
            message.Headers.Add("anthropic-version", "2023-06-01");
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(message, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("The AI service answered {Status}", (int)response.StatusCode);
                return new AiResult(false, null, 0, 0, $"The AI service answered {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var reply = new StringBuilder();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var part in content.EnumerateArray())
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "text" && part.TryGetProperty("text", out var t)) reply.Append(t.GetString());
            int Tokens(string name) => root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var n) && n.TryGetInt32(out var v) ? v : 0;
            return new AiResult(true, reply.ToString(), Tokens("input_tokens"), Tokens("output_tokens"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning("The AI service could not be reached: {Kind}", ex.GetType().Name);
            return new AiResult(false, null, 0, 0, "The AI service could not be reached.");
        }
    }
}
