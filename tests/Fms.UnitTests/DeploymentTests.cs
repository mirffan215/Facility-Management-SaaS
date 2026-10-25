using Fms.BuildingBlocks.Deployment;

namespace Fms.UnitTests;

public sealed class DeploymentTests
{
    private readonly DeploymentOptionsValidator _validator = new();

    [Fact]
    public void SaaS_needs_no_extra_settings() =>
        Assert.True(_validator.Validate(null, new DeploymentOptions { Mode = DeploymentMode.SaaS }).Succeeded);

    [Fact]
    public void OnPrem_requires_single_tenant_and_storage_path()
    {
        var result = _validator.Validate(null, new DeploymentOptions { Mode = DeploymentMode.OnPrem });
        Assert.True(result.Failed);
        Assert.Equal(2, result.Failures!.Count());
    }

    [Fact]
    public void OnPrem_with_settings_is_valid() =>
        Assert.True(_validator.Validate(null, new DeploymentOptions
        {
            Mode = DeploymentMode.OnPrem,
            SingleTenantId = Guid.NewGuid(),
            LocalStoragePath = "/data",
        }).Succeeded);

    [Fact]
    public void Unconfigured_onprem_license_is_not_licensed() =>
        Assert.False(new UnconfiguredOnPremLicenseProvider().Current.Allows("WorkOrders"));

    [Fact]
    public void Saas_license_allows_wildcard_module() =>
        Assert.True(new SaasLicenseProvider().Current.Modules.Contains("*"));
}
