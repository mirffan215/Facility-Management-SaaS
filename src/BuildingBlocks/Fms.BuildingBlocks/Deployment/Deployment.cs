using Microsoft.Extensions.DependencyInjection;
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

/// <summary>What this installation or plan is entitled to. SaaS derives it from the plan; on-prem from a license file.</summary>
public sealed record LicenseInfo(bool IsLicensed, string Edition, IReadOnlySet<string> Modules, DateTimeOffset? ExpiresAt)
{
    public bool Allows(string module) => IsLicensed && Modules.Contains(module);
}

public interface ILicenseProvider
{
    LicenseInfo Current { get; }
}

/// <summary>SaaS Phase 0: every module is enabled. Plan-based entitlements arrive with Administration.</summary>
public sealed class SaasLicenseProvider : ILicenseProvider
{
    public LicenseInfo Current { get; } = new(true, "SaaS", new HashSet<string> { "*" }, null);
}

/// <summary>
/// On-prem placeholder: reports unlicensed until signed license-file validation is built (roadmap phase 8). Nothing
/// is gated on it yet, so it must not be relied on as a control.
/// </summary>
public sealed class UnconfiguredOnPremLicenseProvider : ILicenseProvider
{
    public LicenseInfo Current { get; } = new(false, "Unlicensed", new HashSet<string>(), null);
}

public static class DeploymentServiceCollectionExtensions
{
    public static IServiceCollection AddFmsDeployment(this IServiceCollection services, Action<DeploymentOptions> configure)
    {
        services.AddSingleton<IValidateOptions<DeploymentOptions>, DeploymentOptionsValidator>();
        services.AddOptions<DeploymentOptions>().Configure(configure).ValidateOnStart();

        services.AddSingleton<ILicenseProvider>(sp =>
            sp.GetRequiredService<IOptions<DeploymentOptions>>().Value.Mode == DeploymentMode.OnPrem
                ? new UnconfiguredOnPremLicenseProvider()
                : new SaasLicenseProvider());

        return services;
    }
}
