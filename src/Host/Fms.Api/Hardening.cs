using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Fms.Api;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Networks (CIDR, for example 10.0.0.0/8) whose X-Forwarded-For and X-Forwarded-Proto headers are believed. Empty trusts only loopback.</summary>
    public string[] TrustedProxyCidrs { get; set; } = [];

    public bool Hsts { get; set; } = true;

    /// <summary>The most a request body may be unless an upload endpoint raises it for itself.</summary>
    public long MaxRequestBodyBytes { get; set; } = 5 * 1024 * 1024;

    public RateLimitOptions RateLimit { get; set; } = new();

    public sealed class RateLimitOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Requests a signed-in person may make per minute.</summary>
        public int PerUserPerMinute { get; set; } = 1200;

        /// <summary>Requests per minute from one address that has not signed in.</summary>
        public int AnonymousPerMinute { get; set; } = 300;

        /// <summary>Requests per minute from one address to routes protected only by a secret in the address (visitor passes, calendar feeds, the data feed). This is what makes guessing a secret impractical.</summary>
        public int SecretRoutesPerMinute { get; set; } = 60;
    }
}

public static class Hardening
{
    private static readonly string[] SecretRoutePrefixes = ["/api/v1/odata", "/api/v1/visits/pass", "/api/v1/calendar", "/api/v1/energy/ingest"];

    public static bool IsSecretRoute(string? path) =>
        path is not null && SecretRoutePrefixes.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

    public static IServiceCollection AddFmsHardening(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>().Bind(configuration.GetSection(SecurityOptions.SectionName));
        var security = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            foreach (var cidr in security.TrustedProxyCidrs.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr.Trim()));
            }
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests", detail: "Slow down and try again shortly.",
                    extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" }).ExecuteAsync(context.HttpContext);
            };

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var limits = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.RateLimit;
                var path = http.Request.Path.Value;
                if (!limits.Enabled || path is null || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return RateLimitPartition.GetNoLimiter("none");

                var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (IsSecretRoute(path)) return Fixed("secret:" + address, limits.SecretRoutesPerMinute);

                var subject = http.User.FindFirst("sub")?.Value;
                var provider = http.User.FindFirst(Fms.BuildingBlocks.Identity.FmsClaims.Provider)?.Value;
                return subject is not null && http.User.Identity?.IsAuthenticated == true
                    ? Fixed($"user:{provider}:{subject}", limits.PerUserPerMinute)
                    : Fixed("ip:" + address, limits.AnonymousPerMinute);
            });
        });

        return services;
    }

    private static RateLimitPartition<string> Fixed(string key, int perMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, perMinute), Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });

    /// <summary>Headers every API response carries. The API only ever sends data, so the strictest policy that still works is used.</summary>
    public static IApplicationBuilder UseFmsSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            context.Response.OnStarting(() =>
            {
                // Answers are about one person and one moment: nothing is cached unless an endpoint says so.
                if (!headers.ContainsKey("Cache-Control")) headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            await next(context);
        });
}
