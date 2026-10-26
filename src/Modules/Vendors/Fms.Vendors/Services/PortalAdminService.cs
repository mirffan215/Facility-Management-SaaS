using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

public sealed record InviteRequest(string? Email);

public sealed record InvitationDto(Guid Id, string Email, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt, string State);

/// <summary><see cref="Token"/> is shown here and nowhere else, once. The link to send is <c>/portal/accept#token=…</c>.</summary>
public sealed record InvitationCreatedDto(InvitationDto Invitation, string Token, string Path);

public sealed record PortalUserDto(Guid UserId, string? Name, string? Email, bool IsActive, DateTimeOffset LinkedAt);

public sealed record PortalAccessDto(IReadOnlyList<PortalUserDto> Users, IReadOnlyList<InvitationDto> Invitations);

public sealed record SetPortalUserActiveRequest(bool? Active);

/// <summary>Inviting people from a vendor to the portal and withdrawing their access. Needs vendors.manage.</summary>
public sealed class PortalAdminService(VendorsDbContext db, IGuestInvitations invitations, IUserDirectory users, IAuditPort audit, TimeProvider clock)
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(14);
    private const int MaxOpenInvitations = 20;

    public async Task<Result<InvitationCreatedDto>> InviteAsync(Guid vendorId, InviteRequest r, CancellationToken ct = default)
    {
        var vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vendorId, ct);
        if (vendor is null) return Result<InvitationCreatedDto>.NotFound();
        var email = Common.Clean(r.Email);
        if (email is null || !Common.IsEmail(email)) return Result<InvitationCreatedDto>.Invalid("email", "Enter the person's email address.");
        if (vendor.IsArchived || vendor.Status == VendorStatus.Blocked) return Result<InvitationCreatedDto>.Conflict("This vendor is blocked or archived and cannot be given portal access.");
        var now = clock.GetUtcNow();
        var open = (await invitations.ListAsync(GuestKinds.Vendor, vendorId, ct)).Count(i => i.AcceptedAt is null && i.RevokedAt is null && i.ExpiresAt > now);
        if (open >= MaxOpenInvitations) return Result<InvitationCreatedDto>.Conflict($"This vendor already has {MaxOpenInvitations} open invitations. Revoke some first.");

        var created = await invitations.CreateAsync(GuestKinds.Vendor, vendorId, email, InvitationLifetime, ct);
        audit.Record("vendor.portal_invited", nameof(Vendor), vendorId.ToString(), JsonSerializer.Serialize(new { email, created.Invitation.ExpiresAt }));
        await db.SaveChangesAsync(ct); // the audit event
        return Result<InvitationCreatedDto>.Ok(new InvitationCreatedDto(ToDto(created.Invitation, now), created.Token, "/portal/accept"));
    }

    public async Task<Result<PortalAccessDto>> AccessAsync(Guid vendorId, CancellationToken ct = default)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct)) return Result<PortalAccessDto>.NotFound();
        var now = clock.GetUtcNow();
        var linked = await db.VendorUsers.AsNoTracking().Where(u => u.VendorId == vendorId).OrderBy(u => u.LinkedAt).ToListAsync(ct);
        var people = await users.GetAsync(linked.Select(u => u.UserId), ct);
        var pending = (await invitations.ListAsync(GuestKinds.Vendor, vendorId, ct)).Select(i => ToDto(i, now)).ToList();
        return Result<PortalAccessDto>.Ok(new PortalAccessDto(
            linked.Select(u => new PortalUserDto(u.UserId, people.GetValueOrDefault(u.UserId)?.DisplayName, people.GetValueOrDefault(u.UserId)?.Email, u.IsActive, u.LinkedAt)).ToList(), pending));
    }

    public async Task<Result<PortalAccessDto>> RevokeInvitationAsync(Guid vendorId, Guid invitationId, CancellationToken ct = default)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct)) return Result<PortalAccessDto>.NotFound();
        // Only an invitation that belongs to this vendor can be withdrawn from here.
        if ((await invitations.ListAsync(GuestKinds.Vendor, vendorId, ct)).All(i => i.Id != invitationId)) return Result<PortalAccessDto>.NotFound();
        if (!await invitations.RevokeAsync(invitationId, ct)) return Result<PortalAccessDto>.Conflict("This invitation has already been used or withdrawn.");
        return await AccessAsync(vendorId, ct);
    }

    public async Task<Result<PortalAccessDto>> SetUserActiveAsync(Guid vendorId, Guid userId, bool active, CancellationToken ct = default)
    {
        var link = await db.VendorUsers.FirstOrDefaultAsync(u => u.VendorId == vendorId && u.UserId == userId, ct);
        if (link is null) return Result<PortalAccessDto>.NotFound();
        if (link.IsActive != active)
        {
            link.IsActive = active;
            link.DeactivatedAt = active ? null : clock.GetUtcNow();
            audit.Record(active ? "vendor.portal_user_restored" : "vendor.portal_user_removed", nameof(Vendor), vendorId.ToString(), JsonSerializer.Serialize(new { userId }));
            await db.SaveChangesAsync(ct);
        }

        return await AccessAsync(vendorId, ct);
    }

    /// <summary>Called right after an invitation is accepted. Safe to repeat. A person already belonging to another vendor is refused: one person, one vendor.</summary>
    public async Task<Result<bool>> LinkUserAsync(Guid userId, Guid vendorId, CancellationToken ct = default)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct)) return Result<bool>.NotFound();
        var existing = await db.VendorUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        if (existing is not null) return existing.VendorId == vendorId ? Result<bool>.Ok(true) : Result<bool>.Conflict("This person already belongs to another vendor.");
        db.VendorUsers.Add(new VendorUser { UserId = userId, VendorId = vendorId, LinkedAt = clock.GetUtcNow() });
        audit.Record("vendor.portal_user_linked", nameof(Vendor), vendorId.ToString(), JsonSerializer.Serialize(new { userId }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear(); // the same acceptance twice at once
        }

        return Result<bool>.Ok(true);
    }

    private static InvitationDto ToDto(GuestInvitationRef i, DateTimeOffset now) => new(
        i.Id, i.Email, i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt, i.RevokedAt is not null ? "Revoked" : i.AcceptedAt is not null ? "Accepted" : i.ExpiresAt <= now ? "Expired" : "Open");
}
