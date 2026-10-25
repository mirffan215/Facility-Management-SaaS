namespace Fms.BuildingBlocks.Identity;

/// <summary>
/// Claims that only the server may set. They are stripped from every incoming token, so an identity provider (or an attacker
/// who controls one) can never assert an FMS tenant.
/// </summary>
public static class FmsClaims
{
    public const string TenantId = "fms_tid";
    public const string Provider = "fms_idp";
}

/// <summary>Maps an identity provider's tenant (e.g. an Entra directory id) to the FMS tenant. Unmapped means no access.</summary>
public interface ITenantDirectory
{
    Task<Guid?> ResolveAsync(string provider, string? externalTenantId, CancellationToken ct = default);
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>The identity provider's stable subject id (not unique across providers; combine with <see cref="Provider"/>).</summary>
    string? Subject { get; }

    string? Provider { get; }
    Guid? TenantId { get; }
}
