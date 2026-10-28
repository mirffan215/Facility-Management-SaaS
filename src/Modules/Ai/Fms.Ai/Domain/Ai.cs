using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Ai.Domain;

public enum AiFeature { Triage = 1, Summary = 2 }

public enum AiOutcome { Ok = 1, Failed = 2 }

/// <summary>An organisation's choices about AI. Off until an administrator turns it on.</summary>
public sealed class AiSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public bool Enabled { get; set; }
    public bool TriageEnabled { get; set; } = true;
    public bool SummaryEnabled { get; set; } = true;

    /// <summary>The most suggestions the whole organisation may ask for in a day, so a mistake or misuse cannot run up a bill.</summary>
    public int DailyLimit { get; set; } = AiRules.DefaultDailyLimit;

    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One call to the AI service: who, which suggestion, how much. The words sent and received are deliberately not kept.</summary>
public sealed class AiUsage : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }
    public AiFeature Feature { get; set; }
    public AiOutcome Outcome { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public Guid? SubjectId { get; set; }
}

public sealed record TriageAnswer(string? CategoryCode, string? Priority, double Confidence, string Reason);

/// <summary>Pure rules: cleaning what is sent, and checking what comes back. Tested without a database or a model.</summary>
public static class AiRules
{
    public const int DefaultDailyLimit = 200;
    public const int MaxDailyLimit = 5000;
    public const int PerUserPerHour = 30;
    public const int MaxTextChars = 1500;
    public const int MaxSummaryChars = 1500;
    public const int MaxReasonChars = 240;
    public static readonly string[] Priorities = ["Low", "Medium", "High", "Urgent"];

    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Url = new(@"(https?://|www\.)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Number = new(@"(?<![\w-])\+?\d[\d\s().-]{6,}\d", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>Removes e-mail addresses, web addresses and long numbers (phone, card, identity), drops control characters, squeezes spaces and cuts to a length.</summary>
    public static string Clean(string? text, int max = MaxTextChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.IsControl(c) && c is not ('\n' or '\t') ? ' ' : c);
        var s = sb.ToString();
        s = Email.Replace(s, "[email]");
        s = Url.Replace(s, "[link]");
        s = Number.Replace(s, "[number]");
        s = Space.Replace(s, " ").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    /// <summary>
    /// Reads the model's reply to a triage question. Only a category the organisation really has and one of the four priorities are accepted; anything else is dropped,
    /// so a reply that was steered by the request's own words cannot make up a value. Returns null when there is no usable JSON.
    /// </summary>
    public static TriageAnswer? ParseTriage(string? reply, IReadOnlyCollection<string> categoryCodes)
    {
        var json = FirstObject(reply);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var code = Text("categoryCode")?.Trim();
            code = code is not null && categoryCodes.Contains(code, StringComparer.OrdinalIgnoreCase) ? categoryCodes.First(c => c.Equals(code, StringComparison.OrdinalIgnoreCase)) : null;
            var priority = Priorities.FirstOrDefault(p => p.Equals(Text("priority")?.Trim(), StringComparison.OrdinalIgnoreCase));
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d) ? Math.Clamp(d, 0, 1) : 0;
            var reason = Clean(Text("reason"), MaxReasonChars);
            if (code is null && priority is null) return null;
            return new TriageAnswer(code, priority, Math.Round(confidence, 2), reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first {...} in a reply, since models sometimes wrap the JSON in a sentence or a code fence.</summary>
    public static string? FirstObject(string? reply)
    {
        if (reply is null) return null;
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : null;
    }

    /// <summary>A summary as plain text: control characters and markup characters removed, cut to length.</summary>
    public static string PlainSummary(string? reply)
    {
        var s = Clean(reply?.Replace('<', ' ').Replace('>', ' ').Replace('`', ' ').Replace('*', ' ').Replace('#', ' '), MaxSummaryChars);
        return s;
    }
}
