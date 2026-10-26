namespace Fms.BuildingBlocks.Identity;

/// <summary>
/// Finds the tenant of a person who is not part of any customer's own identity directory, such as a vendor's employee signing in with their company account or a personal one.
/// Such a person has a tenant only because someone there invited them and they accepted. Consulted after the normal tenant directory, never before it.
/// </summary>
public interface IGuestDirectory
{
    Task<Guid?> ResolveAsync(string provider, string subject, CancellationToken ct = default);
}

public sealed record GuestInvitationRef(Guid Id, string Kind, Guid TargetId, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt);

public sealed record CreatedGuestInvitation(GuestInvitationRef Invitation, string Token);

/// <summary>Invitations for outside people, as other modules use them. The token is returned once, when the invitation is made; only a hash of it is kept.</summary>
public interface IGuestInvitations
{
    Task<CreatedGuestInvitation> CreateAsync(string kind, Guid targetId, string email, TimeSpan lifetime, CancellationToken ct = default);

    Task<IReadOnlyList<GuestInvitationRef>> ListAsync(string kind, Guid targetId, CancellationToken ct = default);

    /// <summary>Withdraws an invitation that has not been accepted. Returns false when there is no such open invitation.</summary>
    Task<bool> RevokeAsync(Guid invitationId, CancellationToken ct = default);
}

public static class GuestKinds
{
    public const string Vendor = "Vendor";
}
