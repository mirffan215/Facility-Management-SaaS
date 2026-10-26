using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

/// <summary>Vendors and their contacts. Reading needs vendors.read; changing needs vendors.manage.</summary>
public sealed class VendorService(VendorsDbContext db, IAuditPort audit, TimeProvider clock)
{
    public async Task<Page<VendorDto>> ListAsync(string? q, VendorStatus? status, bool includeArchived, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<VendorDto>.Normalize(page, pageSize);
        var query = db.Vendors.AsNoTracking().Where(v => includeArchived || !v.IsArchived);
        if (status is { } s) query = query.Where(v => v.Status == s);
        if (Common.Clean(q) is { } text) query = query.Where(v => v.Code.Contains(text) || v.Name.Contains(text) || (v.NameAr != null && v.NameAr.Contains(text)) || v.TaxNumber == text);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(v => v.Name).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var counts = await ActiveContractCountsAsync(rows.Select(r => r.Id).ToList(), ct);
        return new Page<VendorDto>(rows.Select(v => ToDto(v, counts.GetValueOrDefault(v.Id))).ToList(), total, number, size);
    }

    public async Task<Result<VendorDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vendor is null) return Result<VendorDto>.NotFound();
        return Result<VendorDto>.Ok(ToDto(vendor, (await ActiveContractCountsAsync([id], ct)).GetValueOrDefault(id)));
    }

    public async Task<Result<IReadOnlyList<VendorContactDto>>> ContactsAsync(Guid vendorId, CancellationToken ct = default)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct)) return Result<IReadOnlyList<VendorContactDto>>.NotFound();
        return Result<IReadOnlyList<VendorContactDto>>.Ok(await ContactDtosAsync(vendorId, ct));
    }

    public async Task<Result<VendorDto>> SaveAsync(Guid? id, SaveVendorRequest r, CancellationToken ct = default)
    {
        Vendor? existing = null;
        if (id is { } vid)
        {
            existing = await db.Vendors.FirstOrDefaultAsync(v => v.Id == vid, ct);
            if (existing is null) return Result<VendorDto>.NotFound();
        }

        var errors = new ValidationErrors();
        if (!Common.IsCode(r.Code)) errors.Add("code", "Code is required: 1-50 letters, digits, dot, dash, slash or underscore.");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (r.TaxNumber is { Length: > 30 }) errors.Add("taxNumber", "Tax number is at most 30 characters.");
        if (r.RegistrationNumber is { Length: > 30 }) errors.Add("registrationNumber", "Registration number is at most 30 characters.");
        if (Common.Clean(r.Email) is { } email && !Common.IsEmail(email)) errors.Add("email", "Enter a valid email address.");
        if (r.Phone is { Length: > 40 }) errors.Add("phone", "Phone is at most 40 characters.");
        if (r.Address is { Length: > 500 }) errors.Add("address", "Address is at most 500 characters.");
        if (Common.Clean(r.Country) is { } country && (country.Length != 2 || !country.All(char.IsAsciiLetter))) errors.Add("country", "Use a two-letter country code, such as SA.");
        if (r.PaymentTerms is { Length: > 200 }) errors.Add("paymentTerms", "Payment terms are at most 200 characters.");
        if (r.Notes is { Length: > 2000 }) errors.Add("notes", "Notes are at most 2000 characters.");
        if (existing is not null && !Common.TryVersion(r.RowVersion, out _)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<VendorDto>();

        var code = r.Code!.Trim().ToUpperInvariant();
        var self = existing?.Id ?? Guid.Empty;
        if (await db.Vendors.AnyAsync(v => v.Code == code && v.Id != self, ct)) return Result<VendorDto>.Conflict("A vendor with this code already exists.");
        var tax = Common.Clean(r.TaxNumber);
        if (tax is not null && await db.Vendors.AnyAsync(v => v.TaxNumber == tax && v.Id != self, ct)) return Result<VendorDto>.Conflict("A vendor with this tax number already exists.");

        var vendor = existing ?? new Vendor { CreatedAt = clock.GetUtcNow() };
        vendor.Code = code;
        vendor.Name = r.Name!.Trim();
        vendor.NameAr = Common.Clean(r.NameAr);
        vendor.TaxNumber = tax;
        vendor.RegistrationNumber = Common.Clean(r.RegistrationNumber);
        vendor.Email = Common.Clean(r.Email);
        vendor.Phone = Common.Clean(r.Phone);
        vendor.Address = Common.Clean(r.Address);
        vendor.Country = Common.Clean(r.Country)?.ToUpperInvariant();
        vendor.PaymentTerms = Common.Clean(r.PaymentTerms);
        vendor.Notes = Common.Clean(r.Notes);
        if (existing is null) db.Vendors.Add(vendor);
        else
        {
            Common.TryVersion(r.RowVersion, out var version);
            db.Entry(vendor).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(vendor).Property(x => x.Name).IsModified = true; // always write the row so a stale version is detected
        }

        audit.Record(existing is null ? "vendor.created" : "vendor.updated", nameof(Vendor), vendor.Id.ToString(), JsonSerializer.Serialize(new { vendor.Code, vendor.Name, vendor.TaxNumber }));
        return await CommitAsync(vendor, ct);
    }

    public async Task<Result<VendorDto>> SetStatusAsync(Guid id, SetVendorStatusRequest r, CancellationToken ct = default)
    {
        var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vendor is null) return Result<VendorDto>.NotFound();
        var errors = new ValidationErrors();
        if (r.Status is not { } status || !Enum.IsDefined(status)) errors.Add("status", "Choose a status.");
        var reason = Common.Clean(r.Reason);
        if (r.Status is not VendorStatus.Active && (reason is null || reason.Length > 500)) errors.Add("reason", "A reason is required to put a vendor on hold or block it (max 500 characters).");
        if (reason is { Length: > 500 }) errors.Add("reason", "Reason is at most 500 characters.");
        if (!Common.TryVersion(r.RowVersion, out var version)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<VendorDto>();

        var from = vendor.Status;
        vendor.Status = r.Status!.Value;
        vendor.StatusReason = vendor.Status == VendorStatus.Active ? null : reason;
        db.Entry(vendor).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record("vendor.status_changed", nameof(Vendor), id.ToString(), JsonSerializer.Serialize(new { from, to = vendor.Status, reason }));
        return await CommitAsync(vendor, ct);
    }

    public async Task<Result<VendorDto>> SetArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (vendor is null) return Result<VendorDto>.NotFound();
        if (!Common.TryVersion(rowVersion, out var version)) return Result<VendorDto>.Invalid("rowVersion", "rowVersion is required.");
        if (archived && (await ActiveContractCountsAsync([id], ct)).GetValueOrDefault(id) > 0) return Result<VendorDto>.Conflict("The vendor still has contracts in force. End them first.");
        vendor.IsArchived = archived;
        db.Entry(vendor).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record(archived ? "vendor.archived" : "vendor.restored", nameof(Vendor), id.ToString());
        return await CommitAsync(vendor, ct);
    }

    public async Task<Result<IReadOnlyList<VendorContactDto>>> SaveContactsAsync(Guid vendorId, SaveContactsRequest r, CancellationToken ct = default)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct)) return Result<IReadOnlyList<VendorContactDto>>.NotFound();
        var input = r.Contacts ?? [];
        var errors = new ValidationErrors();
        if (input.Count > 30) errors.Add("contacts", "A vendor has at most 30 contacts.");
        if (input.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 200)) errors.Add("contacts", "Every contact needs a name (max 200 characters).");
        if (input.Any(c => Common.Clean(c.Email) is { } e && !Common.IsEmail(e))) errors.Add("contacts", "A contact has an invalid email address.");
        if (input.Any(c => c.Role is { Length: > 100 } || c.Phone is { Length: > 40 })) errors.Add("contacts", "A contact's role or phone is too long.");
        if (input.Count(c => c.IsPrimary) > 1) errors.Add("contacts", "Only one contact can be the main one.");
        if (errors.Any) return errors.ToResult<IReadOnlyList<VendorContactDto>>();

        db.Contacts.RemoveRange(await db.Contacts.Where(c => c.VendorId == vendorId).ToListAsync(ct));
        foreach (var c in input)
            db.Contacts.Add(new VendorContact { VendorId = vendorId, Name = c.Name!.Trim(), Role = Common.Clean(c.Role), Email = Common.Clean(c.Email), Phone = Common.Clean(c.Phone), IsPrimary = c.IsPrimary });
        audit.Record("vendor.contacts_saved", nameof(Vendor), vendorId.ToString(), JsonSerializer.Serialize(new { count = input.Count }));
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<VendorContactDto>>.Ok(await ContactDtosAsync(vendorId, ct));
    }

    private async Task<IReadOnlyList<VendorContactDto>> ContactDtosAsync(Guid vendorId, CancellationToken ct) =>
        (await db.Contacts.AsNoTracking().Where(c => c.VendorId == vendorId).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.Name).ToListAsync(ct))
        .Select(c => new VendorContactDto(c.Id, c.Name, c.Role, c.Email, c.Phone, c.IsPrimary)).ToList();

    private async Task<Dictionary<Guid, int>> ActiveContractCountsAsync(List<Guid> vendorIds, CancellationToken ct)
    {
        var today = Common.Today(clock);
        return await db.Contracts.AsNoTracking().Where(c => vendorIds.Contains(c.VendorId) && c.TerminatedAt == null && c.StartOn <= today && c.EndOn >= today)
            .GroupBy(c => c.VendorId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
    }

    private async Task<Result<VendorDto>> CommitAsync(Vendor vendor, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<VendorDto>.Conflict("This vendor was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<VendorDto>.Conflict("A vendor with this code or tax number already exists.");
        }

        return await GetAsync(vendor.Id, ct);
    }

    private static VendorDto ToDto(Vendor v, int activeContracts) => new(
        v.Id, v.Code, v.Name, v.NameAr, v.Status, v.StatusReason, v.TaxNumber, v.RegistrationNumber, v.Email, v.Phone, v.Address, v.Country, v.PaymentTerms, v.Notes, v.ErpAccount,
        v.IsArchived, activeContracts, Common.Version(v.RowVersion));
}
