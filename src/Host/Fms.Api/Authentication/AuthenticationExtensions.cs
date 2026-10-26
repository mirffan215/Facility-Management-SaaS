using System.Security.Claims;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fms.Api.Authentication;

public static class AuthenticationExtensions
{
    public const string RouterScheme = "Fms";
    private const string NoneScheme = "Fms.None";

    /// <summary>
    /// Bearer authentication against any number of OIDC providers (Entra ID, Google, generic OpenID Connect). A policy scheme
    /// routes each token to its provider by issuer; the provider then validates signature, issuer, audience and lifetime.
    /// After validation the server strips any FMS claims the token carried and sets the tenant from the tenant directory.
    /// </summary>
    public static IServiceCollection AddFmsAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<ITenantDirectory, ConfiguredTenantDirectory>();

        var bound = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>();
        var providers = bound?.Providers ?? [];
        var allowHttp = bound?.AllowInsecureAuthority == true && environment.IsDevelopment();

        var builder = services.AddAuthentication(RouterScheme);
        builder.AddScheme<AuthenticationSchemeOptions, NoAuthHandler>(NoneScheme, null);
        builder.AddPolicyScheme(RouterScheme, null, o =>
            o.ForwardDefaultSelector = ctx => SelectScheme(ctx, providers) ?? NoneScheme);

        foreach (var provider in providers)
        {
            builder.AddJwtBearer(provider.SchemeName, o => Configure(o, provider, allowHttp));
        }

        services.AddAuthorization(o =>
        {
            // Everything requires a signed-in user who maps to an FMS tenant; public endpoints opt out with AllowAnonymous.
            o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(FmsClaims.TenantId)
                .Build();
        });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        return services;
    }

    private static string? SelectScheme(HttpContext ctx, List<IdentityProviderOptions> providers)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var handler = new JsonWebTokenHandler();
        var raw = header["Bearer ".Length..].Trim();
        if (!handler.CanReadToken(raw))
        {
            return null;
        }

        // Routing only: the issuer is untrusted here and is checked again, with the signature, by the chosen scheme.
        var issuer = handler.ReadJsonWebToken(raw).Issuer;
        return providers.FirstOrDefault(p => IsValidIssuer(issuer, p))?.SchemeName;
    }

    private static void Configure(JwtBearerOptions o, IdentityProviderOptions provider, bool allowHttp)
    {
        o.Authority = provider.Authority;
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = !allowHttp;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudience = provider.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            IssuerValidator = (issuer, _, _) => IsValidIssuer(issuer, provider)
                ? issuer
                : throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' is not accepted.") { InvalidIssuer = issuer },
        };

        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var identity = (ClaimsIdentity)ctx.Principal!.Identity!;

                foreach (var forged in identity.Claims
                             .Where(c => c.Type is FmsClaims.TenantId or FmsClaims.Provider).ToList())
                {
                    identity.RemoveClaim(forged);
                }

                identity.AddClaim(new Claim(FmsClaims.Provider, provider.Name));

                var external = string.IsNullOrWhiteSpace(provider.ExternalTenantClaim)
                    ? null
                    : identity.FindFirst(provider.ExternalTenantClaim)?.Value;

                var directory = ctx.HttpContext.RequestServices.GetRequiredService<ITenantDirectory>();
                if (await directory.ResolveAsync(provider.Name, external, ctx.HttpContext.RequestAborted) is { } tenantId)
                {
                    identity.AddClaim(new Claim(FmsClaims.TenantId, tenantId.ToString()));
                }
            },
        };
    }

    internal static bool IsValidIssuer(string issuer, IdentityProviderOptions provider) =>
        provider.IssuerPrefix is { } prefix
            ? issuer.StartsWith(prefix, StringComparison.Ordinal)
            : string.Equals(issuer, provider.Issuer ?? provider.Authority, StringComparison.Ordinal);
}

/// <summary>Used when no provider matches the token: never authenticates, so the request gets 401.</summary>
internal sealed class NoAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}

internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;
    public string? Subject => User?.FindFirst("sub")?.Value;
    public string? Provider => User?.FindFirst(FmsClaims.Provider)?.Value;
    public Guid? UserId => accessor.HttpContext?.Items[UserProvisioningMiddleware.UserIdItem] as Guid?;
    public Guid? TenantId => Guid.TryParse(User?.FindFirst(FmsClaims.TenantId)?.Value, out var id) ? id : null;
}
