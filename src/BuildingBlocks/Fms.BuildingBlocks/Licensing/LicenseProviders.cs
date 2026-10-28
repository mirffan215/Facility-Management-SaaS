using System.Reflection;
using System.Text.Json;
using Fms.BuildingBlocks.Deployment;
using Microsoft.Extensions.Options;

namespace Fms.BuildingBlocks.Licensing;

public enum LicenseState
{
    /// <summary>In date.</summary>
    Valid,

    /// <summary>Past its end date but inside the grace period: everything works, with a warning.</summary>
    Grace,

    /// <summary>Past the grace period: data stays readable, changes are refused.</summary>
    Expired,

    NotYetValid,

    /// <summary>The file is damaged, unsigned, signed by an unknown key or its signature does not match.</summary>
    Invalid,

    /// <summary>No license file.</summary>
    Missing,

    /// <summary>Signed for a different installation.</summary>
    WrongInstallation,
}

public sealed class LicenseOptions
{
    public const string SectionName = "License";

    /// <summary>Where the license file is. In a container this is a mounted file or secret.</summary>
    public string Path { get; set; } = "/etc/fms/license/fms.license";

    /// <summary>The license file's text itself (alternative to Path, for environments that only offer environment variables).</summary>
    public string? Text { get; set; }
}

/// <summary>The vendor public keys this build trusts, kept in the assembly so a customer's configuration cannot add one.</summary>
public static class TrustedLicenseKeys
{
    private sealed record Entry(string KeyId, string PublicKey);

    public static IReadOnlyDictionary<string, string> Embedded()
    {
        var assembly = typeof(TrustedLicenseKeys).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("trusted-keys.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        var entries = JsonSerializer.Deserialize<List<Entry>>(stream, LicenseJson.Options) ?? [];
        return entries.ToDictionary(e => e.KeyId, e => e.PublicKey);
    }
}

/// <summary>
/// On-premise entitlements from a signed license file. The file is re-read when it changes (a renewed license applies without a restart);
/// dates are judged on every read, so an installation moves into grace and then read-only on its own.
/// </summary>
public sealed class OnPremLicenseProvider(IOptions<LicenseOptions> options, IOptions<DeploymentOptions> deployment, TimeProvider clock, LicenseVerifier? verifier = null) : ILicenseProvider
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(15);

    private readonly LicenseVerifier _verifier = verifier ?? new LicenseVerifier(TrustedLicenseKeys.Embedded());
    private readonly object _gate = new();
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private (DateTime Stamp, long Length)? _fileStamp;
    private LicensePayload? _payload;
    private LicenseState _fixedState = LicenseState.Missing;
    private string? _detail;

    public LicenseInfo Current
    {
        get
        {
            Refresh();
            return Evaluate(_payload, _fixedState, _detail, clock.GetUtcNow(), deployment.Value.SingleTenantId);
        }
    }

    private void Refresh()
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (now - _checkedAt < CheckEvery) return;
            _checkedAt = now;

            var o = options.Value;
            string? text = null;
            (DateTime, long)? stamp = null;
            if (!string.IsNullOrWhiteSpace(o.Text))
            {
                text = o.Text;
                stamp = (DateTime.MinValue, text.Length);
            }
            else if (File.Exists(o.Path))
            {
                var info = new FileInfo(o.Path);
                stamp = (info.LastWriteTimeUtc, info.Length);
                if (stamp == _fileStamp) return;
                try { text = File.ReadAllText(o.Path); }
                catch (IOException) { return; } // being replaced right now; keep what we have and look again shortly
            }

            if (stamp == _fileStamp && _fileStamp is not null) return;
            _fileStamp = stamp;

            if (text is null)
            {
                (_payload, _fixedState, _detail) = (null, LicenseState.Missing, "No license has been installed.");
                return;
            }

            var (payload, failure) = _verifier.Verify(text);
            if (payload is null)
            {
                (_payload, _fixedState, _detail) = (null, LicenseState.Invalid, failure switch
                {
                    LicenseFailure.UnknownKey => "The license was signed with a key this product does not trust.",
                    LicenseFailure.BadSignature => "The license signature does not match its contents.",
                    _ => "The license file cannot be read.",
                });
                return;
            }

            (_payload, _fixedState, _detail) = (payload, LicenseState.Valid, null);
        }
    }

    /// <summary>Pure judgement of a verified license against the clock and this installation.</summary>
    public static LicenseInfo Evaluate(LicensePayload? payload, LicenseState fixedState, string? detail, DateTimeOffset now, Guid? installationTenant)
    {
        if (payload is null)
        {
            return new LicenseInfo(false, fixedState == LicenseState.Missing ? "Unlicensed" : "Invalid license", new HashSet<string>(), null) { State = fixedState, Detail = detail };
        }

        var modules = new HashSet<string>(payload.Modules, StringComparer.OrdinalIgnoreCase);
        var graceEnds = payload.ExpiresAt.AddDays(Math.Clamp(payload.GraceDays, 0, 90));

        LicenseInfo Make(bool licensed, LicenseState state, string? text) =>
            new(licensed, payload.Edition, modules, payload.ExpiresAt)
            {
                State = state, Customer = payload.Customer, LicenseId = payload.LicenseId, MaxUsers = payload.MaxUsers, GraceEndsAt = graceEnds, Detail = text,
            };

        if (payload.TenantId is { } bound && installationTenant is { } here && bound != here)
        {
            return Make(false, LicenseState.WrongInstallation, "This license was issued for a different installation.");
        }

        if (now < payload.NotBefore) return Make(false, LicenseState.NotYetValid, $"This license starts on {payload.NotBefore:yyyy-MM-dd}.");
        if (now <= payload.ExpiresAt) return Make(true, LicenseState.Valid, null);
        if (now <= graceEnds) return Make(true, LicenseState.Grace, $"This license ended on {payload.ExpiresAt:yyyy-MM-dd}. Everything keeps working until {graceEnds:yyyy-MM-dd}; renew it before then.");
        return Make(false, LicenseState.Expired, $"This license ended on {payload.ExpiresAt:yyyy-MM-dd}. Data can be read but not changed until it is renewed.");
    }
}

/// <summary>Which licensed module a request path belongs to. Anything not listed is core and always available.</summary>
public static class LicenseModules
{
    public const string Work = "work";
    public const string PreventiveMaintenance = "pm";
    public const string Inventory = "inventory";
    public const string Vendors = "vendors";
    public const string Purchasing = "purchasing";
    public const string Spaces = "spaces";
    public const string Bookings = "bookings";
    public const string Visitors = "visitors";
    public const string Reporting = "reporting";
    public const string Compliance = "compliance";
    public const string Energy = "energy";
    public const string Projects = "projects";
    public const string Leases = "leases";

    public static readonly IReadOnlyList<string> All = [Work, PreventiveMaintenance, Inventory, Vendors, Purchasing, Spaces, Bookings, Visitors, Reporting, Compliance, Energy, Projects, Leases];

    private static readonly (string Prefix, string Module)[] Prefixes =
    [
        ("/api/v1/pm", PreventiveMaintenance),
        ("/api/v1/inventory", Inventory),
        ("/api/v1/vendors", Vendors), ("/api/v1/contracts", Vendors), ("/api/v1/contract-documents", Vendors), ("/api/v1/portal", Vendors),
        ("/api/v1/purchasing", Purchasing), ("/api/v1/integration", Purchasing),
        ("/api/v1/spaces", Spaces), ("/api/v1/allocations", Spaces),
        ("/api/v1/resources", Bookings), ("/api/v1/bookings", Bookings), ("/api/v1/calendar", Bookings),
        ("/api/v1/visits", Visitors),
        ("/api/v1/reports", Reporting), ("/api/v1/odata", Reporting),
        ("/api/v1/compliance", Compliance), ("/api/v1/permits", Compliance),
        ("/api/v1/energy", Energy),
        ("/api/v1/projects", Projects),
        ("/api/v1/leases", Leases),
        ("/api/v1/work-orders", Work),
    ];

    /// <summary>The module a path needs, or null for core paths.</summary>
    public static string? For(string? path)
    {
        var p = path ?? string.Empty;
        // Parts used on a work order are the inventory module's, even though they sit under the work order's address.
        if (p.StartsWith("/api/v1/work-orders/", StringComparison.OrdinalIgnoreCase) && p.Contains("/parts", StringComparison.OrdinalIgnoreCase)) return Inventory;
        foreach (var (prefix, module) in Prefixes)
        {
            if (p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) return module;
        }

        return null;
    }
}
