using Fms.BuildingBlocks.Tenancy;

namespace Fms.Reporting.Domain;

/// <summary>
/// Read-only access for an outside tool (Power BI, Excel) to some of the organisation's datasets. Only a hash of the secret is kept; the secret is shown once.
/// Access ends at <see cref="ExpiresAt"/> or when revoked.
/// </summary>
public sealed class FeedToken : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The first characters of the secret, so people can tell tokens apart. Not enough to use.</summary>
    public string Prefix { get; set; } = "";

    public string TokenHash { get; set; } = "";

    /// <summary>"*" for every dataset the organisation offers, or a comma-separated list of dataset names.</summary>
    public string Scope { get; set; } = "*";

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
}
