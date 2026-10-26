using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Fms.Platform.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Fms.IntegrationTests;

/// <summary>The real API over the real database, with signed test tokens. Clients are created per tenant and system role.</summary>
public sealed class ApiHarness : IDisposable
{
    private const string Audience = "api://fms";
    private const string Prefix = "https://login.test/";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly SqlServerFixture _sql;
    private readonly RSA _key = RSA.Create(2048);
    private readonly Dictionary<Guid, string> _external = [];
    private readonly Action<IServiceCollection>? _configureServices;

    public WebApplicationFactory<Program> Factory { get; }

    public ApiHarness(SqlServerFixture sql, Action<IServiceCollection>? configureServices = null, IDictionary<string, string?>? settings = null)
    {
        _sql = sql;
        _configureServices = configureServices;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", sql.AppConnectionString);
            b.UseSetting("Authentication:Providers:0:Name", "entra");
            b.UseSetting("Authentication:Providers:0:Authority", "https://login.test/common/v2.0");
            b.UseSetting("Authentication:Providers:0:Audience", Audience);
            b.UseSetting("Authentication:Providers:0:IssuerPrefix", Prefix);
            b.UseSetting("Authentication:Providers:0:ExternalTenantClaim", "tid");
            foreach (var (k, v) in settings ?? new Dictionary<string, string?>()) b.UseSetting(k, v);
            b.ConfigureServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>("idp:entra", o =>
                {
                    var config = new OpenIdConnectConfiguration();
                    config.SigningKeys.Add(new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "k1" });
                    o.Configuration = config;
                    o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                });
                _configureServices?.Invoke(services);
            });
        });
    }

    public void Dispose()
    {
        Factory.Dispose();
        _key.Dispose();
    }

    /// <summary>A signed-in client for a new user of <paramref name="tenant"/> holding the given system role (or none).</summary>
    public async Task<HttpClient> ClientAsync(Guid tenant, string? role)
    {
        var external = await ExternalTenantAsync(tenant);
        var subject = "u-" + Guid.NewGuid().ToString("N");

        await using (var platform = _sql.App(null, platform: true))
        {
            var user = new User { TenantId = tenant, Provider = "entra", Subject = subject, CreatedAt = DateTimeOffset.UtcNow, LastSignInAt = DateTimeOffset.UtcNow };
            platform.Users.Add(user);
            if (role is not null)
            {
                var roleId = await platform.Roles.IgnoreQueryFilters().Where(r => r.TenantId == tenant && r.Name == role).Select(r => r.Id).SingleAsync();
                platform.UserRoles.Add(new UserRole { TenantId = tenant, UserId = user.Id, RoleId = roleId, GrantedAt = DateTimeOffset.UtcNow });
            }

            await platform.SaveChangesAsync();
        }

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"{Prefix}{external}/v2.0",
            Audience = Audience,
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = external },
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// A signed-in person whose identity directory is mapped to no tenant (a vendor's employee with their own company account). They get a tenant only by accepting an invitation.
    /// The same client keeps working after that, because the tenant is worked out from the token on every request.
    /// </summary>
    public HttpClient GuestClient(string? subject = null)
    {
        subject ??= "g-" + Guid.NewGuid().ToString("N");
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"{Prefix}{Guid.NewGuid()}/v2.0",
            Audience = Audience,
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = Guid.NewGuid().ToString(), ["email"] = subject + "@vendor.test", ["name"] = "Guest " + subject[^4..] },
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> ExternalTenantAsync(Guid tenant)
    {
        if (_external.TryGetValue(tenant, out var existing)) return existing;
        var external = Guid.NewGuid().ToString();
        await using var platform = _sql.App(null, platform: true);
        platform.TenantIdentityMappings.Add(new TenantIdentityMapping { TenantId = tenant, Provider = "entra", ExternalTenantId = external });
        await platform.SaveChangesAsync();
        return _external[tenant] = external;
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient c, string url, object body) => c.PostAsJsonAsync(url, body, Json);
    public static Task<HttpResponseMessage> PutAsync(HttpClient c, string url, object body) => c.PutAsJsonAsync(url, body, Json);
    public static async Task<T> ReadAsync<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>(Json))!;
}
