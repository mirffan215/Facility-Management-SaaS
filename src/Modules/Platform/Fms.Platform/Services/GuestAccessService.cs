using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Fms.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Services;

public sealed record AcceptedInvitation(Guid TenantId, Guid UserId, string Kind, Guid TargetId);

/// <summary>
/// Invitations for outside people and the sign-ins they create. <b>Security notes.</b> A token is 256 random bits, shown once, stored only as a hash, valid for a limited time and
/// usable by one sign-in. Accepting needs a valid token <i>and</i> a signed-in identity from a configured provider; it links exactly that identity to exactly the inviting tenant,
/// grants the Vendor role (which holds no staff permission), and refuses an identity already linked to another tenant. Failures say only that the invitation is not valid.
/// </summary>
public sealed class GuestAccessService(PlatformDbContext db, ICurrentUser user, TenantContext tenantContext, TimeProvider clock) : IGuestInvitations, IGuestDirectory
{
    private const string Invalid = "This invitation is not valid. Ask for a new one.";

    // ---------------- inviting (tenant staff) ----------------

    public async Task<CreatedGuestInvitation> CreateAsync(string kind, Guid targetId, string email, TimeSpan lifetime, CancellationToken ct = default)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        var invitation = new GuestInvitation
        {
            Kind = kind, TargetId = targetId, Email = email.Trim(), TokenHash = Hash(token), CreatedBy = user.UserId, CreatedAt = now, ExpiresAt = now + lifetime,
        };
        db.GuestInvitations.Add(invitation);
        db.PendingAudit(user, "guest.invited", nameof(GuestInvitation), invitation.Id.ToString(), JsonSerializer.Serialize(new { kind, targetId, email = invitation.Email, invitation.ExpiresAt }));
        await db.SaveChangesAsync(ct);
        return new CreatedGuestInvitation(ToRef(invitation), token);
    }

    public async Task<IReadOnlyList<GuestInvitationRef>> ListAsync(string kind, Guid targetId, CancellationToken ct = default) =>
        (await db.GuestInvitations.AsNoTracking().Where(i => i.Kind == kind && i.TargetId == targetId).OrderByDescending(i => i.CreatedAt).Take(50).ToListAsync(ct)).Select(ToRef).ToList();

    public async Task<bool> RevokeAsync(Guid invitationId, CancellationToken ct = default)
    {
        var invitation = await db.GuestInvitations.FirstOrDefaultAsync(i => i.Id == invitationId && i.AcceptedAt == null && i.RevokedAt == null, ct);
        if (invitation is null) return false;
        invitation.RevokedAt = clock.GetUtcNow();
        db.PendingAudit(user, "guest.invitation_revoked", nameof(GuestInvitation), invitationId.ToString(), null);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---------------- accepting (the invited person, who has no tenant yet) ----------------

    public async Task<Result<AcceptedInvitation>> AcceptAsync(string? token, string provider, string subject, string? email, string? displayName, Guid? signedInTenant, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 20 or > 200) return Result<AcceptedInvitation>.NotFound(Invalid);
        using var platform = tenantContext.BeginPlatformScope(); // the tenant is not known until the token says which one
        var hash = Hash(token.Trim());
        var invitation = await db.GuestInvitations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || invitation.RevokedAt is not null) return Result<AcceptedInvitation>.NotFound(Invalid);

        // Someone already working in one organisation cannot be moved into another by an invitation.
        if (signedInTenant is { } current && current != invitation.TenantId) return Result<AcceptedInvitation>.Conflict("You are signed in to a different organisation. Use a different sign-in for this invitation.");

        var now = clock.GetUtcNow();
        if (invitation.AcceptedAt is not null)
        {
            // The same sign-in asking again (a reload, a retry after a failure further along) gets the same answer; nobody else can use it.
            return invitation.AcceptedProvider == provider && invitation.AcceptedSubject == subject && invitation.AcceptedUserId is { } existing
                ? Result<AcceptedInvitation>.Ok(new AcceptedInvitation(invitation.TenantId, existing, invitation.Kind, invitation.TargetId))
                : Result<AcceptedInvitation>.NotFound(Invalid);
        }

        if (invitation.ExpiresAt < now) return Result<AcceptedInvitation>.NotFound(Invalid);

        var guest = await db.GuestIdentities.FirstOrDefaultAsync(g => g.Provider == provider && g.Subject == subject, ct);
        if (guest is not null && guest.TenantId != invitation.TenantId)
            return Result<AcceptedInvitation>.Conflict("This sign-in is already linked to another organisation. Use a different sign-in for this invitation.");

        if (!await db.Roles.IgnoreQueryFilters().AnyAsync(r => r.TenantId == invitation.TenantId && r.Name == SystemRoles.Vendor, ct))
            await SystemRoleSync.EnsureAsync(db, invitation.TenantId, ct);
        var role = await db.Roles.IgnoreQueryFilters().FirstAsync(r => r.TenantId == invitation.TenantId && r.Name == SystemRoles.Vendor, ct);

        if (guest is null) db.GuestIdentities.Add(new GuestIdentity { Provider = provider, Subject = subject, TenantId = invitation.TenantId, InvitationId = invitation.Id, CreatedAt = now });

        var person = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.TenantId == invitation.TenantId && u.Provider == provider && u.Subject == subject, ct);
        if (person is null)
        {
            person = new User
            {
                TenantId = invitation.TenantId, Provider = provider, Subject = subject, Email = Trim(email ?? invitation.Email, 320), DisplayName = Trim(displayName, 200), CreatedAt = now, LastSignInAt = now,
            };
            db.Users.Add(person);
        }
        else if (!person.IsActive)
        {
            return Result<AcceptedInvitation>.Forbidden("This account has been deactivated.");
        }

        if (!await db.UserRoles.IgnoreQueryFilters().AnyAsync(r => r.UserId == person.Id && r.RoleId == role.Id, ct))
            db.UserRoles.Add(new UserRole { TenantId = invitation.TenantId, UserId = person.Id, RoleId = role.Id, GrantedBy = invitation.CreatedBy, GrantedAt = now });

        invitation.AcceptedAt = now;
        invitation.AcceptedProvider = provider;
        invitation.AcceptedSubject = subject;
        invitation.AcceptedUserId = person.Id;
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = invitation.TenantId, OccurredAt = now, ActorId = person.Id, Action = "guest.invitation_accepted", EntityType = nameof(GuestInvitation), EntityId = invitation.Id.ToString(),
            ChangeSummaryJson = JsonSerializer.Serialize(new { invitation.Kind, invitation.TargetId, provider }),
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two accepts raced (the same person twice, or another person with the same token): only one wins.
            db.ChangeTracker.Clear();
            var again = await db.GuestInvitations.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
            return again?.AcceptedProvider == provider && again.AcceptedSubject == subject && again.AcceptedUserId is { } uid
                ? Result<AcceptedInvitation>.Ok(new AcceptedInvitation(again.TenantId, uid, again.Kind, again.TargetId))
                : Result<AcceptedInvitation>.NotFound(Invalid);
        }

        return Result<AcceptedInvitation>.Ok(new AcceptedInvitation(invitation.TenantId, person.Id, invitation.Kind, invitation.TargetId));
    }

    // ---------------- resolving a guest's tenant at sign-in ----------------

    public async Task<Guid?> ResolveAsync(string provider, string subject, CancellationToken ct = default) =>
        await (from g in db.GuestIdentities.AsNoTracking()
               join t in db.Tenants on g.TenantId equals t.Id
               where g.Provider == provider && g.Subject == subject && t.IsActive
               select (Guid?)t.Id).FirstOrDefaultAsync(ct);

    private static GuestInvitationRef ToRef(GuestInvitation i) => new(i.Id, i.Kind, i.TargetId, i.Email, i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt);

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

internal static class AuditShortcut
{
    /// <summary>Records an audit event in the same save, in the current tenant, through the buffer the context drains.</summary>
    public static void PendingAudit(this PlatformDbContext db, ICurrentUser user, string action, string entityType, string entityId, string? json)
    {
        // The audit port is a module-wide singleton buffer; here we are inside the platform module itself, so add the row directly.
        db.AuditEvents.Add(new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, ActorId = user.UserId, Action = action, EntityType = entityType, EntityId = entityId, ChangeSummaryJson = json });
    }
}
