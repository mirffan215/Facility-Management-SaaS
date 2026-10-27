using System.Globalization;
using System.Text;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Services;

public sealed record AuditEntryDto(Guid Id, DateTimeOffset OccurredAt, Guid? ActorId, string? ActorName, string Action, string EntityType, string? EntityId, string? Summary, string? CorrelationId);

public sealed record AuditQuery(DateOnly? From, DateOnly? To, Guid? ActorId, string? Action, string? EntityType, string? EntityId, int? Page, int? PageSize);

/// <summary>
/// Reading the append-only audit trail: who did what, to what, when. Search by period, person, action and thing; export what was found. The trail itself can never be edited here
/// or anywhere (the table refuses updates and deletes). Exporting is itself written to the trail.
/// </summary>
public sealed class AuditViewer(PlatformDbContext db, IUserDirectory users, IAuditPort audit, TimeProvider clock)
{
    public const int DefaultDays = 7;
    public const int MaxDays = 366;
    public const int MaxExportRows = 10_000;

    public async Task<Result<Page<AuditEntryDto>>> SearchAsync(AuditQuery q, CancellationToken ct = default)
    {
        var range = Range(q, out var error);
        if (error is not null) return Result<Page<AuditEntryDto>>.Invalid("to", error);
        var (number, size) = Page<AuditEntryDto>.Normalize(q.Page, q.PageSize);
        var query = Filter(q, range.From, range.To);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return Result<Page<AuditEntryDto>>.Ok(new Page<AuditEntryDto>(await ToDtosAsync(rows, ct), total, number, size));
    }

    /// <summary>The kinds of event in a period, for the filter's choices.</summary>
    public async Task<IReadOnlyList<string>> ActionsAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var range = Range(new AuditQuery(from, to, null, null, null, null, null, null), out _);
        return await Filter(new AuditQuery(null, null, null, null, null, null, null, null), range.From, range.To).Select(e => e.Action).Distinct().OrderBy(a => a).Take(300).ToListAsync(ct);
    }

    public async Task<Result<(string Csv, int Rows, bool Truncated)>> ExportAsync(AuditQuery q, CancellationToken ct = default)
    {
        var range = Range(q, out var error);
        if (error is not null) return Result<(string, int, bool)>.Invalid("to", error);
        var rows = await Filter(q, range.From, range.To).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).Take(MaxExportRows + 1).ToListAsync(ct);
        var truncated = rows.Count > MaxExportRows;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        var dtos = await ToDtosAsync(rows, ct);

        audit.Record("audit.exported", nameof(AuditEvent), null, System.Text.Json.JsonSerializer.Serialize(new { from = range.From, to = range.To, q.Action, q.EntityType, q.ActorId, rows = dtos.Count, truncated }));
        await db.SaveChangesAsync(ct);
        return Result<(string, int, bool)>.Ok((ToCsv(dtos), dtos.Count, truncated));
    }

    private IQueryable<AuditEvent> Filter(AuditQuery q, DateTimeOffset from, DateTimeOffset to)
    {
        var query = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAt >= from && e.OccurredAt < to);
        if (q.ActorId is { } a) query = query.Where(e => e.ActorId == a);
        if (!string.IsNullOrWhiteSpace(q.Action)) { var action = q.Action.Trim(); query = query.Where(e => e.Action == action || e.Action.StartsWith(action + ".")); }
        if (!string.IsNullOrWhiteSpace(q.EntityType)) { var type = q.EntityType.Trim(); query = query.Where(e => e.EntityType == type); }
        if (!string.IsNullOrWhiteSpace(q.EntityId)) { var id = q.EntityId.Trim(); query = query.Where(e => e.EntityId == id); }
        return query;
    }

    private (DateTimeOffset From, DateTimeOffset To) Range(AuditQuery q, out string? error)
    {
        error = null;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var to = q.To ?? today;
        var from = q.From ?? to.AddDays(-(DefaultDays - 1));
        if (to < from) error = "The end cannot be before the start.";
        else if (to.DayNumber - from.DayNumber + 1 > MaxDays) error = $"Search at most {MaxDays} days at a time.";
        return (new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    private async Task<IReadOnlyList<AuditEntryDto>> ToDtosAsync(List<AuditEvent> rows, CancellationToken ct)
    {
        var names = await users.GetAsync(rows.Where(r => r.ActorId != null).Select(r => r.ActorId!.Value).Distinct(), ct);
        return rows.Select(r => new AuditEntryDto(r.Id, r.OccurredAt, r.ActorId, r.ActorId is { } u && names.TryGetValue(u, out var p) ? p.DisplayName ?? p.Email : null, r.Action, r.EntityType, r.EntityId, r.ChangeSummaryJson, r.CorrelationId)).ToList();
    }

    /// <summary>One row per event. Cells that start with a formula character are neutralised so a spreadsheet cannot run something typed into a title.</summary>
    public static string ToCsv(IEnumerable<AuditEntryDto> rows)
    {
        var sb = new StringBuilder("﻿");
        sb.Append("OccurredAt,Actor,ActorId,Action,EntityType,EntityId,Summary,CorrelationId\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(',', Cell(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)), Cell(r.ActorName ?? ""), Cell(r.ActorId?.ToString() ?? ""), Cell(r.Action), Cell(r.EntityType),
                Cell(r.EntityId ?? ""), Cell(r.Summary ?? ""), Cell(r.CorrelationId ?? ""))).Append("\r\n");
        return sb.ToString();
    }

    public static string Cell(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
