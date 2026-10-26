using Fms.Api.Authentication;

namespace Fms.UnitTests;

public sealed class AuthOptionsTests
{
    private static IdentityProviderOptions Provider(string name = "p") =>
        new() { Name = name, Authority = "https://idp.example", Audience = "api" };

    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static bool Valid(AuthOptions o, string environment = "Production") => new AuthOptionsValidator(new Env(environment)).Validate(null, o).Succeeded;

    [Fact]
    public void No_providers_is_valid() => Assert.True(Valid(new AuthOptions()));

    [Fact]
    public void Valid_provider_and_mapping() =>
        Assert.True(Valid(new AuthOptions
        {
            Providers = [Provider()],
            TenantMappings = [new TenantMapping { Provider = "p", ExternalTenantId = "x", TenantId = Guid.NewGuid() }],
        }));

    [Fact]
    public void Authority_must_be_https() =>
        Assert.False(Valid(new AuthOptions { Providers = [new IdentityProviderOptions { Name = "p", Authority = "http://idp", Audience = "api" }] }));

    [Fact]
    public void Audience_is_required()
    {
        var p = Provider();
        p.Audience = "";
        Assert.False(Valid(new AuthOptions { Providers = [p] }));
    }

    [Fact]
    public void Duplicate_names_are_rejected() =>
        Assert.False(Valid(new AuthOptions { Providers = [Provider("a"), Provider("A")] }));

    [Fact]
    public void Multi_tenant_provider_requires_a_tenant_claim()
    {
        var p = Provider();
        p.IssuerPrefix = "https://idp.example/";
        Assert.False(Valid(new AuthOptions { Providers = [p] }));
        p.ExternalTenantClaim = "tid";
        Assert.True(Valid(new AuthOptions { Providers = [p] }));
    }

    [Fact]
    public void Mapping_to_an_unknown_provider_is_rejected() =>
        Assert.False(Valid(new AuthOptions
        {
            Providers = [Provider()],
            TenantMappings = [new TenantMapping { Provider = "nope", ExternalTenantId = "x", TenantId = Guid.NewGuid() }],
        }));

    [Fact]
    public void Http_authority_needs_the_insecure_switch_and_the_development_environment()
    {
        var http = new IdentityProviderOptions { Name = "p", Authority = "http://localhost:9099", Audience = "api" };

        Assert.False(Valid(new AuthOptions { Providers = [http] }, "Development"));
        Assert.True(Valid(new AuthOptions { Providers = [http], AllowInsecureAuthority = true }, "Development"));
        Assert.False(Valid(new AuthOptions { Providers = [http], AllowInsecureAuthority = true }, "Production"));
        Assert.False(Valid(new AuthOptions { AllowInsecureAuthority = true }, "Production")); // the switch itself is refused outside Development
        Assert.False(Valid(new AuthOptions { Providers = [new IdentityProviderOptions { Name = "p", Authority = "ftp://x", Audience = "a" }], AllowInsecureAuthority = true }, "Development"));
    }
}
