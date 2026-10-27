using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Fms.BuildingBlocks.Deployment;

public enum DeploymentMode
{
    /// <summary>Hosted multi-tenant service.</summary>
    SaaS,

    /// <summary>Customer-hosted, single tenant.</summary>
    OnPrem,
}

public sealed class DeploymentOptions
{
    public const string SectionName = "Deployment";

    public DeploymentMode Mode { get; set; } = DeploymentMode.SaaS;

    /// <summary>On-premise only: the one tenant this installation serves. Required when Mode is OnPrem.</summary>
    public Guid? SingleTenantId { get; set; }

    /// <summary>On-premise only: local storage root for files.</summary>
    public string? LocalStoragePath { get; set; }
}

public sealed class DeploymentOptionsValidator : IValidateOptions<DeploymentOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentOptions options)
    {
        if (options.Mode != DeploymentMode.OnPrem)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();
        if (options.SingleTenantId is null || options.SingleTenantId == Guid.Empty)
        {
            errors.Add("Deployment:SingleTenantId is required when Deployment:Mode is OnPrem.");
        }

        if (string.IsNullOrWhiteSpace(options.LocalStoragePath))
        {
            errors.Add("Deployment:LocalStoragePath is required when Deployment:Mode is OnPrem.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>What this installation or plan is entitled to. SaaS derives it from the plan; on-prem from a signed license file.</summary>
public sealed record LicenseInfo(bool IsLicensed, string Edition, IReadOnlySet<string> Modules, DateTimeOffset? ExpiresAt)
{
    public Licensing.LicenseState State { get; init; } = IsLicensed ? Licensing.LicenseState.Valid : Licensing.LicenseState.Missing;

    public string? Customer { get; init; }

    public string? LicenseId { get; init; }

    /// <summary>Most active users, or null for no limit.</summary>
    public int? MaxUsers { get; init; }

    /// <summary>End of the grace period after expiry; null when there is no expiry.</summary>
    public DateTimeOffset? GraceEndsAt { get; init; }

    /// <summary>A short, safe explanation for people (never the file contents).</summary>
    public string? Detail { get; init; }

    public bool Allows(string module) => IsLicensed && (Modules.Contains("*") || Modules.Contains(module));

    /// <summary>Without a valid license the installation keeps its data readable (so it can be exported) but accepts no changes.</summary>
    public bool AllowsChanges => IsLicensed;
}

public interface ILicenseProvider
{
    LicenseInfo Current { get; }
}

/// <summary>SaaS: every module is enabled. Per-tenant plan entitlements are not built yet.</summary>
public sealed class SaasLicenseProvider : ILicenseProvider
{
    public LicenseInfo Current { get; } = new(true, "SaaS", new HashSet<string> { "*" }, null);
}

/// <summary>On-prem with no license source at all. Read-only.</summary>
public sealed class UnconfiguredOnPremLicenseProvider : ILicenseProvider
{
    public LicenseInfo Current { get; } = new(false, "Unlicensed", new HashSet<string>(), null) { State = Licensing.LicenseState.Missing, Detail = "No license has been installed." };
}

public static class DeploymentServiceCollectionExtensions
{
    public static IServiceCollection AddFmsDeployment(this IServiceCollection services, Action<DeploymentOptions> configure, Action<Licensing.LicenseOptions>? configureLicense = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<Licensing.LicenseOptions>().Configure(o => configureLicense?.Invoke(o));
        services.AddSingleton<IValidateOptions<DeploymentOptions>, DeploymentOptionsValidator>();
        services.AddOptions<DeploymentOptions>().Configure(configure).ValidateOnStart();

        services.AddSingleton<ILicenseProvider>(sp =>
            sp.GetRequiredService<IOptions<DeploymentOptions>>().Value.Mode == DeploymentMode.OnPrem
                ? ActivatorUtilities.CreateInstance<Licensing.OnPremLicenseProvider>(sp)
                : new SaasLicenseProvider());

        return services;
    }
}
