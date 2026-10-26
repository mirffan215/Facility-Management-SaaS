using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fms.Vendors.Services;

/// <summary>Finds contracts that are about to end or have ended and queues one reminder each. Called by the worker; each tenant in its own scope.</summary>
public sealed class ContractMonitor(IServiceScopeFactory scopes, ILogger<ContractMonitor> log)
{
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        List<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var today = Common.Today(scope.ServiceProvider.GetRequiredService<TimeProvider>());
            var horizon = today.AddDays(366);
            tenants = await scope.ServiceProvider.GetRequiredService<VendorsDbContext>().Contracts.IgnoreQueryFilters()
                .Where(c => c.TerminatedAt == null && c.EndOn <= horizon && (c.ExpiringNoticedAt == null || c.ExpiredNoticedAt == null)).Select(c => c.TenantId).Distinct().ToListAsync(ct);
        }

        var queued = 0;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
                queued += await scope.ServiceProvider.GetRequiredService<ContractAlerts>().QueueAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Contract reminders failed for a tenant; the others continue");
            }
        }

        return queued;
    }
}

public sealed class ContractAlerts(VendorsDbContext db, IOutbox outbox, TimeProvider clock)
{
    /// <summary>Queues a reminder for each contract newly inside its renewal notice, and one for each that has newly ended. Saves once, so the flag and the message go together.</summary>
    public async Task<int> QueueAsync(CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var now = clock.GetUtcNow();
        var queued = 0;
        foreach (var c in await db.Contracts.Where(x => x.TerminatedAt == null && x.StartOn <= today).ToListAsync(ct))
        {
            if (today > c.EndOn)
            {
                if (c.ExpiredNoticedAt is not null) continue;
                c.ExpiredNoticedAt = now;
                c.ExpiringNoticedAt ??= now; // no separate "ending soon" after the fact
                outbox.Enqueue(VendorMessages.ContractAlert, new ContractAlertMessage(c.Id, ContractAlertKind.Expired));
                queued++;
            }
            else if (c.ExpiringNoticedAt is null && c.EndOn.DayNumber - today.DayNumber <= c.RenewalNoticeDays)
            {
                c.ExpiringNoticedAt = now;
                outbox.Enqueue(VendorMessages.ContractAlert, new ContractAlertMessage(c.Id, ContractAlertKind.Expiring));
                queued++;
            }
        }

        if (queued > 0) await db.SaveChangesAsync(ct);
        return queued;
    }
}

/// <summary>Tells the people who manage vendors that a contract is ending or has ended.</summary>
public sealed class ContractAlertHandler(VendorsDbContext db, IUserDirectory users, INotificationSink sink, TimeProvider clock) : IOutboxHandler
{
    public string MessageType => VendorMessages.ContractAlert;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<ContractAlertMessage>();
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == m.ContractId, ct);
        if (contract is null || contract.TerminatedAt is not null) return;
        var vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == contract.VendorId, ct);
        var days = ContractRules.DaysLeft(contract, Common.Today(clock));
        var en = m.Kind == ContractAlertKind.Expired ? "has ended" : $"ends in {days} day(s)";
        var ar = m.Kind == ContractAlertKind.Expired ? "انتهى" : $"ينتهي خلال {days} يوم";
        var name = vendor?.Name ?? "";
        var subject = $"Contract {contract.Number} {en}";
        var body = $"{contract.Number} · {contract.Title} ({name}) {en}. End date {contract.EndOn:yyyy-MM-dd}.\n\nالعقد {contract.Number} · {contract.Title} ({vendor?.NameAr ?? name}) {ar}. تاريخ الانتهاء {contract.EndOn:yyyy-MM-dd}.";
        var who = await users.WithPermissionAsync(Permissions.VendorsManage, ct);
        await sink.NotifyAsync(message.MessageId, who.Select(u => new NotificationRequest(
            u.Id, "contract." + m.Kind, new { number = contract.Number, title = contract.Title, vendor = name, days, contractId = contract.Id }, $"/vendors?tab=contracts&contract={contract.Id}", subject, body)).ToList(), ct);
    }
}
