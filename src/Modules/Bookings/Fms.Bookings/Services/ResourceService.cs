using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Bookings.Domain;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Bookings.Services;

public sealed partial class ResourceService(BookingsDbContext db, IFacilityLookup facilities, IAuditPort audit, TimeProvider clock)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,49}$")]
    private static partial Regex CodePattern();

    public async Task<IReadOnlyList<ResourceDto>> ListAsync(ResourceKind? kind, string? q, bool includeArchived, CancellationToken ct = default)
    {
        var query = db.Resources.AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(r => !r.IsArchived);
        if (kind is { } k) query = query.Where(r => r.Kind == k);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(r => r.Code.Contains(term) || r.Name.Contains(term) || (r.NameAr != null && r.NameAr.Contains(term)));
        }

        var rows = await query.OrderBy(r => r.Kind).ThenBy(r => r.Code).Take(500).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    public async Task<Result<ResourceDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await db.Resources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? Result<ResourceDto>.NotFound() : Result<ResourceDto>.Ok((await ToDtosAsync([r], ct))[0]);
    }

    public async Task<Result<ResourceDto>> SaveAsync(Guid? id, SaveResourceRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var kind = r.Kind ?? ResourceKind.Other;
        if (r.Kind is not { } given || !Enum.IsDefined(given)) errors.Add("kind", "Choose what kind of resource this is.");
        var code = Common.Clean(r.Code);
        if (code is null || !CodePattern().IsMatch(code)) errors.Add("code", "Code is 1 to 50 letters, digits, dots, dashes, underscores or slashes.");
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200) errors.Add("name", "Name is required (at most 200 characters).");
        var nameAr = Common.Clean(r.NameAr);
        if (nameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        var zone = Common.Clean(r.TimeZone) ?? "UTC";
        if (!BookingRules.IsZone(zone)) errors.Add("timeZone", "Unknown time zone. Use a name like Asia/Riyadh.");
        var capacity = r.Capacity ?? 1;
        if (capacity is < 1 or > 10_000) errors.Add("capacity", "Capacity is from 1 to 10000.");
        var notes = Common.Clean(r.Notes);
        if (notes is { Length: > 1000 }) errors.Add("notes", "Notes are at most 1000 characters.");

        var opens = ParseTime(r.OpensAt ?? "07:00", out var opensOk);
        var closes = ParseTime(r.ClosesAt ?? "19:00", out var closesOk);
        if (!opensOk || r.OpensAt?.Trim() == "24:00") errors.Add("opensAt", "Use a time like 08:00.");
        if (!closesOk) errors.Add("closesAt", "Use a time like 18:00 (24:00 for midnight).");
        else if (opensOk && closes != new TimeOnly(0, 0) && closes <= opens) errors.Add("closesAt", "It must close after it opens.");

        var days = (r.Days ?? Enum.GetValues<DayOfWeek>()).Distinct().ToList();
        if (days.Count == 0) errors.Add("days", "Choose at least one day.");
        var mask = days.Aggregate(0, (m, d) => m | BookingRules.DayBit(d));

        var min = r.MinMinutes ?? 15;
        var max = r.MaxMinutes ?? 480;
        var ahead = r.MaxAdvanceDays ?? 90;
        var buffer = r.BufferMinutes ?? 0;
        var grace = r.CheckInGraceMinutes ?? 15;
        if (min < 5 || min % 5 != 0 || min > 1440) errors.Add("minMinutes", "The shortest booking is a multiple of 5 minutes, at least 5.");
        if (max < min || max % 5 != 0 || max > 1440) errors.Add("maxMinutes", "The longest booking is a multiple of 5 minutes, at least the shortest and at most 24 hours.");
        if (ahead is < 1 or > 365) errors.Add("maxAdvanceDays", "Advance booking is from 1 to 365 days.");
        if (buffer is < 0 or > 480 || buffer % 5 != 0) errors.Add("bufferMinutes", "The gap between bookings is a multiple of 5 minutes, up to 480.");
        if (grace is < 0 or > 240) errors.Add("checkInGraceMinutes", "The check-in grace period is from 0 to 240 minutes.");

        if (r.SpaceId is { } space && (await facilities.GetLocationAsync(space, ct)) is not { Kind: "Space" }) errors.Add("spaceId", "That space does not exist.");
        if (errors.Any) return errors.ToResult<ResourceDto>();

        BookableResource entity;
        if (id is null)
        {
            entity = new BookableResource { CreatedAt = clock.GetUtcNow() };
            db.Resources.Add(entity);
        }
        else
        {
            var existing = await db.Resources.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (existing is null) return Result<ResourceDto>.NotFound();
            if (!Common.TryVersion(r.RowVersion, out var version)) return Result<ResourceDto>.Invalid("rowVersion", "Reload the resource and try again.");
            db.Entry(existing).Property(x => x.RowVersion).OriginalValue = version;
            entity = existing;
        }

        entity.Kind = kind;
        entity.Code = code!;
        entity.Name = name!;
        entity.NameAr = nameAr;
        entity.SpaceId = r.SpaceId;
        entity.Capacity = capacity;
        entity.TimeZone = zone;
        entity.OpensAt = opens;
        entity.ClosesAt = closes;
        entity.DaysMask = mask;
        entity.MinMinutes = min;
        entity.MaxMinutes = max;
        entity.MaxAdvanceDays = ahead;
        entity.BufferMinutes = buffer;
        entity.RequiresApproval = r.RequiresApproval ?? false;
        entity.CheckInRequired = r.CheckInRequired ?? false;
        entity.CheckInGraceMinutes = grace;
        entity.Notes = notes;
        if (id is not null) db.Entry(entity).Property(x => x.Name).IsModified = true; // always write the row so a stale version is detected
        audit.Record(id is null ? "resource.created" : "resource.updated", nameof(BookableResource), entity.Id.ToString(), JsonSerializer.Serialize(new { entity.Code, entity.Name, entity.Kind, entity.RequiresApproval }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ResourceDto>.Conflict("Someone else changed this resource. Reload and try again.");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result<ResourceDto>.Conflict("A resource with this code already exists.");
        }

        return Result<ResourceDto>.Ok((await ToDtosAsync([entity], ct))[0]);
    }

    /// <summary>Archiving stops new bookings; existing ones stand.</summary>
    public async Task<Result<ResourceDto>> SetArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var entity = await db.Resources.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return Result<ResourceDto>.NotFound();
        if (!Common.TryVersion(rowVersion, out var version)) return Result<ResourceDto>.Invalid("rowVersion", "Reload the resource and try again.");
        db.Entry(entity).Property(x => x.RowVersion).OriginalValue = version;
        entity.IsArchived = archived;
        audit.Record(archived ? "resource.archived" : "resource.restored", nameof(BookableResource), id.ToString(), JsonSerializer.Serialize(new { entity.Code }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ResourceDto>.Conflict("Someone else changed this resource. Reload and try again.");
        }

        return Result<ResourceDto>.Ok((await ToDtosAsync([entity], ct))[0]);
    }

    private static TimeOnly ParseTime(string text, out bool ok)
    {
        ok = false;
        text = text.Trim();
        if (text == "24:00") { ok = true; return new TimeOnly(0, 0); }
        if (TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) { ok = true; return t; }
        return default;
    }

    private async Task<IReadOnlyList<ResourceDto>> ToDtosAsync(List<BookableResource> rows, CancellationToken ct)
    {
        var spaces = await facilities.LocationsAsync(rows.Where(r => r.SpaceId is not null).Select(r => r.SpaceId!.Value), ct);
        return rows.Select(r => new ResourceDto(
            r.Id, r.Kind, r.Code, r.Name, r.NameAr, r.SpaceId, r.SpaceId is { } s && spaces.TryGetValue(s, out var f) ? $"{f.Code} · {f.Name}" : null, r.Capacity, r.TimeZone,
            r.OpensAt.ToString("HH\\:mm", CultureInfo.InvariantCulture), r.ClosesAt == new TimeOnly(0, 0) ? "24:00" : r.ClosesAt.ToString("HH\\:mm", CultureInfo.InvariantCulture),
            Enum.GetValues<DayOfWeek>().Where(d => (r.DaysMask & BookingRules.DayBit(d)) != 0).ToList(), r.MinMinutes, r.MaxMinutes, r.MaxAdvanceDays, r.BufferMinutes,
            r.RequiresApproval, r.CheckInRequired, r.CheckInGraceMinutes, r.Notes, r.IsArchived, Common.Version(r.RowVersion))).ToList();
    }
}
