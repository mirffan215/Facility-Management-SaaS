using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Energy.Domain;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Energy.Services;

/// <summary>Rules that watch a meter: using more than a limit in a day, or sending nothing for too long.</summary>
public sealed class AlarmService(EnergyDbContext db, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxRules = 2000;

    public async Task<IReadOnlyList<AlarmRuleDto>> RulesAsync(CancellationToken ct = default)
    {
        var rules = await db.AlarmRules.AsNoTracking().OrderBy(r => r.CreatedAt).Take(MaxRules).ToListAsync(ct);
        var meters = await MetersAsync(rules.Select(r => r.MeterId), ct);
        return rules.Select(r => ToDto(r, meters.GetValueOrDefault(r.MeterId))).ToList();
    }

    public async Task<Result<AlarmRuleDto>> SaveAsync(Guid? id, SaveAlarmRuleRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose what to watch for.");
        if (r.Threshold is not { } threshold || threshold <= 0 || threshold > EnergyMath.MaxValue) errors.Add("threshold", "Enter a limit above zero.");
        else if (r.Kind == AlarmKind.NoReading && threshold > 24 * 90) errors.Add("threshold", "At most 2160 hours (90 days).");
        EnergyMeter? meter = null;
        if (r.MeterId is not { } meterId || (meter = await db.Meters.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meterId && !m.IsArchived, ct)) is null) errors.Add("meterId", "Choose a meter.");
        if (errors.Any) return errors.ToResult<AlarmRuleDto>();

        AlarmRule? rule;
        if (id is null)
        {
            if (await db.AlarmRules.CountAsync(ct) >= MaxRules) return Result<AlarmRuleDto>.Conflict($"At most {MaxRules} alarm rules.");
            if (await db.AlarmRules.AnyAsync(x => x.MeterId == meter!.Id && x.Kind == r.Kind, ct)) return Result<AlarmRuleDto>.Conflict("That meter already has a rule of this kind. Change it instead.");
            rule = new AlarmRule { MeterId = meter!.Id, Kind = r.Kind!.Value, CreatedAt = clock.GetUtcNow(), CreatedBy = user.UserId };
            db.AlarmRules.Add(rule);
        }
        else
        {
            rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (rule is null) return Result<AlarmRuleDto>.NotFound();
            if (!Common.TryVersion(r.RowVersion, out var version)) return Result<AlarmRuleDto>.Invalid("rowVersion", "Reload and try again.");
            db.Entry(rule).Property(x => x.RowVersion).OriginalValue = version;
            if (rule.MeterId != meter!.Id || rule.Kind != r.Kind) return Result<AlarmRuleDto>.Invalid("meterId", "A rule's meter and kind cannot change. Make a new rule.");
        }

        rule.Threshold = Math.Round(r.Threshold!.Value, 4);
        rule.IsActive = r.IsActive ?? true;
        if (!rule.IsActive) { rule.IsRaised = false; rule.RaisedAt = null; }
        audit.Record(id is null ? "energy.alarm_rule_created" : "energy.alarm_rule_updated", nameof(AlarmRule), rule.Id.ToString(), JsonSerializer.Serialize(new { meter.Code, rule.Kind, rule.Threshold, rule.IsActive }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<AlarmRuleDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<AlarmRuleDto>.Ok(ToDto(rule, meter));
    }

    public async Task<Page<AlarmEventDto>> EventsAsync(Guid? meterId, int? page, int? pageSize, CancellationToken ct = default)
    {
        var query = db.AlarmEvents.AsNoTracking().AsQueryable();
        if (meterId is { } id) query = query.Where(e => e.MeterId == id);
        var total = await query.CountAsync(ct);
        var (number, size) = Page<AlarmEventDto>.Normalize(page, pageSize);
        var rows = await query.OrderByDescending(e => e.At).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var meters = await MetersAsync(rows.Select(e => e.MeterId), ct);
        return new Page<AlarmEventDto>(rows.Select(e => new AlarmEventDto(e.RuleId, e.MeterId, meters.GetValueOrDefault(e.MeterId)?.Code ?? "", e.At, e.Kind, e.Value)).ToList(), total, number, size);
    }

    private async Task<Dictionary<Guid, EnergyMeter>> MetersAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Meters.AsNoTracking().Where(m => list.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
    }

    private static AlarmRuleDto ToDto(AlarmRule r, EnergyMeter? m) => new(r.Id, r.MeterId, m?.Code ?? "", m?.Name ?? "", r.Kind, r.Threshold, r.IsActive, r.IsRaised, r.RaisedAt, Common.Version(r.RowVersion));
}

/// <summary>Checks every active rule of one organisation, raising and clearing alarms and telling the energy managers when one is raised.</summary>
public sealed class AlarmEvaluator(EnergyDbContext db, ConsumptionService consumption, IUserDirectory users, INotificationSink sink, TimeProvider clock)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var raised = 0;
        var rules = await db.AlarmRules.Where(r => r.IsActive || r.IsRaised).ToListAsync(ct);
        var meters = await db.Meters.AsNoTracking().Where(m => rules.Select(r => r.MeterId).Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        List<DirectoryUser>? managers = null;

        foreach (var rule in rules)
        {
            if (!meters.TryGetValue(rule.MeterId, out var meter) || meter.IsArchived) continue;
            var (breached, value) = await EvaluateAsync(rule, meter, now, ct);
            if (rule.IsActive && breached && !rule.IsRaised)
            {
                rule.IsRaised = true;
                rule.RaisedAt = now;
                db.AlarmEvents.Add(new AlarmEvent { RuleId = rule.Id, MeterId = meter.Id, At = now, Kind = AlarmEventKind.Raised, Value = value });
                managers ??= (await users.WithPermissionAsync(Permissions.EnergyManage, ct)).ToList();
                var (en, ar) = rule.Kind == AlarmKind.HighDailyUse
                    ? ($"High use on {meter.Name}: {value:0.##} {meter.Unit} yesterday (limit {rule.Threshold:0.##})", $"استهلاك مرتفع على {meter.Name}: {value:0.##} {meter.Unit} أمس (الحد {rule.Threshold:0.##})")
                    : ($"No reading from {meter.Name} for {value:0} hours (limit {rule.Threshold:0.##})", $"لا توجد قراءة من {meter.Name} منذ {value:0} ساعة (الحد {rule.Threshold:0.##})");
                var requests = managers.Select(u => new NotificationRequest(u.Id, "energy." + (rule.Kind == AlarmKind.HighDailyUse ? "highuse" : "noreading"), new { meterId = meter.Id, title = meter.Name, value }, $"/energy?tab=alarms", en, en + "\n\n" + ar)).ToList();
                if (requests.Count > 0) await sink.NotifyAsync(SourceId(rule.Id, now), requests, ct);
                raised++;
            }
            else if (rule.IsRaised && (!breached || !rule.IsActive))
            {
                rule.IsRaised = false;
                rule.RaisedAt = null;
                db.AlarmEvents.Add(new AlarmEvent { RuleId = rule.Id, MeterId = meter.Id, At = now, Kind = AlarmEventKind.Cleared, Value = value });
            }
        }

        await db.SaveChangesAsync(ct);
        return raised;
    }

    private async Task<(bool Breached, decimal? Value)> EvaluateAsync(AlarmRule rule, EnergyMeter meter, DateTimeOffset now, CancellationToken ct)
    {
        if (rule.Kind == AlarmKind.HighDailyUse)
        {
            var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
            var use = await consumption.ComputeAsync(yesterday, yesterday, null, null, meter.Id, ct);
            var total = use.IsOk ? use.Value!.Meters.FirstOrDefault()?.Total ?? 0 : 0;
            return (total > rule.Threshold, total);
        }

        var latest = await db.Readings.AsNoTracking().Where(r => r.MeterId == meter.Id && !db.Voids.Any(v => v.ReadingId == r.Id)).OrderByDescending(r => r.At).Select(r => (DateTimeOffset?)r.At).FirstOrDefaultAsync(ct);
        var since = latest ?? meter.CreatedAt;
        var hours = (decimal)(now - since).TotalHours;
        return (hours > rule.Threshold, Math.Round(hours, 1));
    }

    /// <summary>One alarm raised on one day gives one id, so a run repeated after a crash does not tell people twice.</summary>
    public static Guid SourceId(Guid ruleId, DateTimeOffset at) => new(MD5.HashData(Encoding.UTF8.GetBytes($"energy:{ruleId:N}:{at:yyyyMMddHH}")));
}

/// <summary>Called by the worker; each organisation in its own scope.</summary>
public sealed class EnergyMonitor(IServiceScopeFactory scopes, ILogger<EnergyMonitor> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            tenants = await scope.ServiceProvider.GetRequiredService<EnergyDbContext>().AlarmRules.IgnoreQueryFilters().Where(r => r.IsActive || r.IsRaised).Select(r => r.TenantId).Distinct().ToListAsync(ct);
        }

        var raised = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
                raised += await scope.ServiceProvider.GetRequiredService<AlarmEvaluator>().RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Energy alarms failed for a tenant; the others continue");
            }
        }

        return raised;
    }
}
