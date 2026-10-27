using System.Security.Cryptography;
using System.Text;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Reporting.Domain;
using Fms.Reporting.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Reporting.Services;

public sealed record FeedTokenDto(Guid Id, string Name, string Prefix, string Scope, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, string State);

public sealed record CreateFeedTokenRequest(string? Name, int? ExpiresInDays, IReadOnlyList<string>? Datasets);

/// <summary>A new token with its secret. The secret is shown now and never again.</summary>
public sealed record CreatedFeedTokenDto(FeedTokenDto Token, string Secret, string FeedPath);

public sealed record DatasetInfoDto(string Name, IReadOnlyList<DatasetColumn> Columns, string? TimeColumn);

public sealed class FeedService(ReportingDbContext db, IEnumerable<IDatasetCatalog> catalogs, TenantContext tenant, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxOpenTokens = 20;
    public const int MaxDays = 1095;
    public const string SecretPrefix = "fmsf_";

    public IReadOnlyList<IDatasetProvider> AllDatasets => catalogs.SelectMany(c => c.Datasets).OrderBy(d => d.Name).ToList();

    public IReadOnlyList<DatasetInfoDto> Offered() => AllDatasets.Select(d => new DatasetInfoDto(d.Name, d.Columns, d.TimeColumn)).ToList();

    public async Task<IReadOnlyList<FeedTokenDto>> ListAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return (await db.FeedTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).Take(200).ToListAsync(ct)).Select(t => ToDto(t, now)).ToList();
    }

    public async Task<Result<CreatedFeedTokenDto>> CreateAsync(CreateFeedTokenRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var name = string.IsNullOrWhiteSpace(r.Name) ? null : r.Name.Trim();
        if (name is null || name.Length > 100) errors.Add("name", "Name the tool this is for (at most 100 characters).");
        var days = r.ExpiresInDays ?? 365;
        if (days is < 1 or > MaxDays) errors.Add("expiresInDays", $"Access lasts from 1 to {MaxDays} days.");
        var offered = AllDatasets.Select(d => d.Name).ToHashSet();
        var chosen = (r.Datasets ?? []).Distinct().ToList();
        if (chosen.Any(c => !offered.Contains(c))) errors.Add("datasets", "One of the chosen datasets does not exist.");
        if (errors.Any) return errors.ToResult<CreatedFeedTokenDto>();

        var now = clock.GetUtcNow();
        if (await db.FeedTokens.CountAsync(t => t.RevokedAt == null && t.ExpiresAt > now, ct) >= MaxOpenTokens) return Result<CreatedFeedTokenDto>.Conflict($"At most {MaxOpenTokens} tokens can be active. Revoke one first.");

        var secret = SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var token = new FeedToken
        {
            Name = name!, Prefix = secret[..10], TokenHash = Hash(secret), Scope = chosen.Count == 0 || chosen.Count == offered.Count ? "*" : string.Join(',', chosen.Order()),
            CreatedBy = user.UserId, CreatedAt = now, ExpiresAt = now.AddDays(days),
        };
        db.FeedTokens.Add(token);
        audit.Record("feed.token_created", nameof(FeedToken), token.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { token.Name, token.Scope, token.ExpiresAt }));
        await db.SaveChangesAsync(ct);
        return Result<CreatedFeedTokenDto>.Ok(new CreatedFeedTokenDto(ToDto(token, now), secret, "/api/v1/odata"));
    }

    public async Task<Result<FeedTokenDto>> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var t = await db.FeedTokens.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<FeedTokenDto>.NotFound();
        if (t.RevokedAt is null)
        {
            t.RevokedAt = clock.GetUtcNow();
            t.RevokedBy = user.UserId;
            audit.Record("feed.token_revoked", nameof(FeedToken), id.ToString(), System.Text.Json.JsonSerializer.Serialize(new { t.Name }));
            await db.SaveChangesAsync(ct);
        }

        return Result<FeedTokenDto>.Ok(ToDto(t, clock.GetUtcNow()));
    }

    /// <summary>
    /// Works out who a feed request is for from the secret it carries (HTTP Basic with the secret as the password, or an X-Api-Key header), and makes the rest of the request
    /// belong to that tenant. Null for anything invalid, always the same answer. The caller has no sign-in, so the token is looked up across organisations first.
    /// </summary>
    public async Task<FeedAccess?> AuthenticateAsync(string? authorization, string? apiKey, CancellationToken ct = default)
    {
        var secret = SecretFrom(authorization, apiKey);
        if (secret is null || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length is < 20 or > 100) return null;
        var hash = Hash(secret);
        var now = clock.GetUtcNow();

        Guid tenantId;
        string scope;
        using (tenant.BeginPlatformScope())
        {
            var t = await db.FeedTokens.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
            if (t is null || t.RevokedAt is not null || t.ExpiresAt <= now) return null;
            if (t.LastUsedAt is null || now - t.LastUsedAt > TimeSpan.FromMinutes(1))
            {
                t.LastUsedAt = now;
                await db.SaveChangesAsync(ct);
            }

            tenantId = t.TenantId;
            scope = t.Scope;
        }

        tenant.SetTenant(tenantId);
        var allowed = scope == "*" ? null : scope.Split(',').ToHashSet();
        return new FeedAccess(tenantId, allowed);
    }

    public static string? SecretFrom(string? authorization, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon < 0 ? decoded : decoded[(colon + 1)..]; // the user name is ignored; the secret is the password
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static FeedTokenDto ToDto(FeedToken t, DateTimeOffset now) =>
        new(t.Id, t.Name, t.Prefix, t.Scope, t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt, t.RevokedAt is not null ? "Revoked" : t.ExpiresAt <= now ? "Expired" : "Active");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}

/// <summary>Who a feed request is acting for: the tenant, and the datasets it may read (null means all).</summary>
public sealed record FeedAccess(Guid TenantId, IReadOnlySet<string>? Datasets);
