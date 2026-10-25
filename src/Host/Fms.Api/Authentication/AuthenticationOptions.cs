using Microsoft.Extensions.Options;

namespace Fms.Api.Authentication;

public sealed class AuthOptions
{
    public const string SectionName = "Authentication";

    public List<IdentityProviderOptions> Providers { get; set; } = [];

    /// <summary>SaaS: which provider tenant belongs to which FMS tenant. Ignored on-prem (one tenant).</summary>
    public List<TenantMapping> TenantMappings { get; set; } = [];
}

public sealed class IdentityProviderOptions
{
    /// <summary>Short unique name, e.g. "entra", "google", "corp-oidc".</summary>
    public string Name { get; set; } = "";

    public string Authority { get; set; } = "";

    /// <summary>The API audience tokens must be issued for.</summary>
    public string Audience { get; set; } = "";

    /// <summary>Exact issuer. Defaults to the authority.</summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// For multi-tenant providers (Entra "common"): issuers must start with this prefix. Tokens are still only accepted when
    /// the tenant they carry is in <see cref="AuthOptions.TenantMappings"/>.
    /// </summary>
    public string? IssuerPrefix { get; set; }

    /// <summary>Claim holding the provider's tenant (Entra: "tid"). Empty for providers with a single tenant.</summary>
    public string? ExternalTenantClaim { get; set; }

    /// <summary>Public client id the web app signs in with. Returned by the auth config endpoint.</summary>
    public string? ClientId { get; set; }

    public string SchemeName => $"idp:{Name}";
}

public sealed class TenantMapping
{
    public string Provider { get; set; } = "";
    public string ExternalTenantId { get; set; } = "";
    public Guid TenantId { get; set; }
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in options.Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !p.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                errors.Add("Each identity provider needs a Name of letters, digits, '-' or '_'.");
            }
            else if (!names.Add(p.Name))
            {
                errors.Add($"Identity provider name '{p.Name}' is used twice.");
            }

            if (!Uri.TryCreate(p.Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"Provider '{p.Name}': Authority must be an absolute https URL.");
            }

            if (string.IsNullOrWhiteSpace(p.Audience))
            {
                errors.Add($"Provider '{p.Name}': Audience is required.");
            }

            if (p.IssuerPrefix is not null && string.IsNullOrWhiteSpace(p.ExternalTenantClaim))
            {
                errors.Add($"Provider '{p.Name}': a multi-tenant provider (IssuerPrefix) needs ExternalTenantClaim, otherwise any tenant of that provider could sign in.");
            }
        }

        foreach (var m in options.TenantMappings)
        {
            if (!names.Contains(m.Provider) || string.IsNullOrWhiteSpace(m.ExternalTenantId) || m.TenantId == Guid.Empty)
            {
                errors.Add($"Tenant mapping for provider '{m.Provider}' is incomplete or names an unknown provider.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
