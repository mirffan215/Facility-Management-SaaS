using System.Security.Cryptography;
using System.Text;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Leases.Domain;
using Fms.Leases.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Leases.Services;

/// <summary>Warns people when a lease approaches a notice date, a break date or its end, and when it has run out. Called by the worker; each tenant in its own scope.</summary>
public sealed class LeaseMonitor(IServiceScopeFactory scopes, ILogger<LeaseMonitor> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            tenants = await scope.ServiceProvider.GetRequiredService<LeasesDbContext>().Leases.IgnoreQueryFilters().Where(l => l.Status == LeaseStatus.Active).Select(l => l.TenantId).Distinct().ToListAsync(ct);
        }

        var sent = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
                sent += await scope.ServiceProvider.GetRequiredService<LeaseAlerts>().RunAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Lease alerts failed for a tenant; the others continue");
            }
        }

        return sent;
    }
}

public sealed class LeaseAlerts(LeasesDbContext db, IUserDirectory users, INotificationSink sink, TimeProvider clock)
{
    /// <summary>While a deadline is near, the warning comes again this often.</summary>
    public const int RepeatDays = 7;

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var managers = (await users.WithPermissionAsync(Permissions.LeasesManage, ct)).ToList();
        var sent = 0;
        foreach (var l in await db.Leases.Where(x => x.Status == LeaseStatus.Active).ToListAsync(ct))
        {
            var key = LeaseMath.AlertKey(l, today);
            if (key is null)
            {
                l.LastAlertKey = null;
                l.LastAlertOn = null;
                continue;
            }

            var changed = l.LastAlertKey != key;
            var repeat = !key.StartsWith("expired", StringComparison.Ordinal) && l.LastAlertOn is { } last && today.DayNumber - last.DayNumber >= RepeatDays;
            if (!changed && !repeat) continue;

            var kind = key[..key.IndexOf(':')];
            var on = DateOnly.ParseExact(key[(key.IndexOf(':') + 1)..], "yyyy-MM-dd");
            var (en, ar) = kind switch
            {
                "notice" => ($"Notice date approaching: {l.Reference} {l.Title}. Give notice by {on:yyyy-MM-dd}.", $"يقترب موعد الإشعار: {l.Reference} {l.Title}. يجب تقديم الإشعار قبل {on:yyyy-MM-dd}."),
                "break" => ($"Break notice date approaching: {l.Reference} {l.Title}. Decide by {on:yyyy-MM-dd}.", $"يقترب موعد إشعار الإنهاء المبكر: {l.Reference} {l.Title}. القرار قبل {on:yyyy-MM-dd}."),
                "expiry" => ($"Lease ending: {l.Reference} {l.Title} ends on {on:yyyy-MM-dd}.", $"اقتراب انتهاء العقد: {l.Reference} {l.Title} ينتهي في {on:yyyy-MM-dd}."),
                _ => ($"Lease has run out: {l.Reference} {l.Title} ended on {on:yyyy-MM-dd}. Extend or terminate it.", $"انتهى العقد: {l.Reference} {l.Title} انتهى في {on:yyyy-MM-dd}. مدّده أو أنهِه."),
            };
            var requests = managers.Select(m => new NotificationRequest(m.Id, "lease." + kind, new { leaseId = l.Id, reference = l.Reference, title = l.Title, on = on.ToString("yyyy-MM-dd") }, $"/leases?lease={l.Id}", en, en + "\n\n" + ar)).ToList();
            if (requests.Count > 0) sent += await sink.NotifyAsync(SourceId(l.Id, key, today), requests, ct);
            l.LastAlertKey = key;
            l.LastAlertOn = today;
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>The same lease, deadline and day always give the same id, so a run repeated after a crash does not double-notify.</summary>
    public static Guid SourceId(Guid leaseId, string key, DateOnly day) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"lease:{leaseId:N}:{key}:{day:yyyyMMdd}")));
}
