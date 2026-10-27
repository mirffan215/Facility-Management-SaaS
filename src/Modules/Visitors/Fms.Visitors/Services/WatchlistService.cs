using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Visitors.Domain;
using Fms.Visitors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Visitors.Services;

/// <summary>People security wants a second look at. Entries are never deleted: removing one keeps who removed it, when and why.</summary>
public sealed class WatchlistService(VisitorsDbContext db, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxEntries = 5000;

    public async Task<IReadOnlyList<WatchlistDto>> ListAsync(CancellationToken ct = default) =>
        (await db.Watchlist.AsNoTracking().Where(w => w.RemovedAt == null).OrderBy(w => w.Name).Take(MaxEntries).ToListAsync(ct)).Select(w => new WatchlistDto(w.Id, w.Name, w.Reason, w.AddedAt)).ToList();

    public async Task<Result<WatchlistDto>> AddAsync(AddWatchlistRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200 || VisitorRules.Normalize(name).Length == 0) errors.Add("name", "Give the person's name (at most 200 characters).");
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) errors.Add("reason", "A reason is required (at most 500 characters).");
        if (errors.Any) return errors.ToResult<WatchlistDto>();

        var key = VisitorRules.Normalize(name);
        if (await db.Watchlist.AnyAsync(w => w.NameKey == key && w.RemovedAt == null, ct)) return Result<WatchlistDto>.Conflict("This person is already on the watchlist.");
        if (await db.Watchlist.CountAsync(w => w.RemovedAt == null, ct) >= MaxEntries) return Result<WatchlistDto>.Conflict($"The watchlist can hold at most {MaxEntries} people.");

        var entry = new WatchlistEntry { Name = name!, NameKey = key, Reason = reason!, AddedBy = user.UserId, AddedAt = clock.GetUtcNow() };
        db.Watchlist.Add(entry);
        audit.Record("watchlist.added", nameof(WatchlistEntry), entry.Id.ToString(), JsonSerializer.Serialize(new { entry.Name }));
        await db.SaveChangesAsync(ct);
        return Result<WatchlistDto>.Ok(new WatchlistDto(entry.Id, entry.Name, entry.Reason, entry.AddedAt));
    }

    public async Task<Result<WatchlistDto>> RemoveAsync(Guid id, RemoveWatchlistRequest r, CancellationToken ct = default)
    {
        var entry = await db.Watchlist.FirstOrDefaultAsync(w => w.Id == id && w.RemovedAt == null, ct);
        if (entry is null) return Result<WatchlistDto>.NotFound();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<WatchlistDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        entry.RemovedAt = clock.GetUtcNow();
        entry.RemovedBy = user.UserId;
        entry.RemoveReason = reason;
        audit.Record("watchlist.removed", nameof(WatchlistEntry), id.ToString(), JsonSerializer.Serialize(new { entry.Name, reason }));
        await db.SaveChangesAsync(ct);
        return Result<WatchlistDto>.Ok(new WatchlistDto(entry.Id, entry.Name, entry.Reason, entry.AddedAt));
    }
}

public sealed class VisitorSettingsService(VisitorsDbContext db, IAuditPort audit, TimeProvider clock)
{
    public async Task<VisitorSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? new VisitorSettingsDto(false, null, 365, null) : new VisitorSettingsDto(s.RequireInduction, s.InductionText, s.RetentionDays, Common.Version(s.RowVersion));
    }

    public async Task<Result<VisitorSettingsDto>> SaveAsync(SaveVisitorSettingsRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var text = Common.Clean(r.InductionText);
        if (text is { Length: > 4000 }) errors.Add("inductionText", "The induction text is at most 4000 characters.");
        var require = r.RequireInduction ?? false;
        if (require && text is null) errors.Add("inductionText", "Write the induction text contractors must acknowledge.");
        var days = r.RetentionDays ?? 365;
        if (days < VisitorRules.MinRetentionDays || days > VisitorRules.MaxRetentionDays) errors.Add("retentionDays", $"Keep visitor details between {VisitorRules.MinRetentionDays} and {VisitorRules.MaxRetentionDays} days.");
        if (errors.Any) return errors.ToResult<VisitorSettingsDto>();

        var s = await db.Settings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new VisitorSettings();
            db.Settings.Add(s);
        }
        else
        {
            if (!Common.TryVersion(r.RowVersion, out var version)) return Result<VisitorSettingsDto>.Invalid("rowVersion", "Reload and try again.");
            db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
        }

        s.RequireInduction = require;
        s.InductionText = text;
        s.RetentionDays = days;
        s.UpdatedAt = clock.GetUtcNow();
        audit.Record("visitors.settings_saved", nameof(VisitorSettings), s.Id.ToString(), JsonSerializer.Serialize(new { require, days }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result<VisitorSettingsDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<VisitorSettingsDto>.Ok(await GetAsync(ct));
    }
}
