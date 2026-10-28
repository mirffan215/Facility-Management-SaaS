using System.Security.Cryptography;
using System.Text;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Compliance.Domain;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Compliance.Services;

/// <summary>Warns people when an obligation becomes due soon, overdue or failed. Called by the worker; each tenant in its own scope.</summary>
public sealed class ComplianceMonitor(IServiceScopeFactory scopes, ILogger<ComplianceMonitor> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            tenants = await scope.ServiceProvider.GetRequiredService<ComplianceDbContext>().Obligations.IgnoreQueryFilters().Where(o => !o.IsArchived).Select(o => o.TenantId).Distinct().ToListAsync(ct);
        }

        var sent = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
                sent += await scope.ServiceProvider.GetRequiredService<ComplianceAlerts>().RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Compliance alerts failed for a tenant; the others continue");
            }
        }

        return sent;
    }
}

public sealed class ComplianceAlerts(ComplianceDbContext db, IUserDirectory users, INotificationSink sink, TimeProvider clock)
{
    public const int RepeatOverdueDays = 7;

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var managers = (await users.WithPermissionAsync(Permissions.ComplianceManage, ct)).ToList();
        var sent = 0;
        foreach (var o in await db.Obligations.Where(x => !x.IsArchived).ToListAsync(ct))
        {
            var status = ComplianceRules.StatusOf(o, today);
            if (status is not (ComplianceStatus.DueSoon or ComplianceStatus.Overdue or ComplianceStatus.Failed))
            {
                o.LastAlertStatus = null;
                o.LastAlertOn = null;
                continue;
            }

            var changed = o.LastAlertStatus != status;
            var repeat = status == ComplianceStatus.Overdue && o.LastAlertOn is { } last && today.DayNumber - last.DayNumber >= RepeatOverdueDays;
            if (!changed && !repeat) continue;

            var people = new List<Guid>();
            if (o.OwnerId is { } owner) people.Add(owner);
            if (people.Count == 0 || status == ComplianceStatus.Failed) people.AddRange(managers.Select(m => m.Id));
            var due = ComplianceRules.DueOn(o);
            var (en, ar) = status switch
            {
                ComplianceStatus.DueSoon => ($"Due soon: {o.Title} (by {due:yyyy-MM-dd})", $"يقترب موعده: {o.Title} (حتى {due:yyyy-MM-dd})"),
                ComplianceStatus.Overdue => ($"Overdue: {o.Title} (was due {due:yyyy-MM-dd})", $"متأخر: {o.Title} (كان مستحقًا في {due:yyyy-MM-dd})"),
                _ => ($"Failed: {o.Title}", $"لم ينجح: {o.Title}"),
            };
            var requests = people.Distinct().Select(id => new NotificationRequest(id, "compliance." + status.ToString().ToLowerInvariant(), new { obligationId = o.Id, title = o.Title, dueOn = due?.ToString("yyyy-MM-dd") }, $"/compliance?obligation={o.Id}", en, en + "\n\n" + ar)).ToList();
            if (requests.Count > 0) sent += await sink.NotifyAsync(SourceId(o.Id, status, today), requests, ct);
            o.LastAlertStatus = status;
            o.LastAlertOn = today;
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>The same obligation, state and day always give the same id, so a run repeated after a crash does not double-notify.</summary>
    public static Guid SourceId(Guid obligationId, ComplianceStatus status, DateOnly day) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"compliance:{obligationId:N}:{(int)status}:{day:yyyyMMdd}")));
}
