using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Cloud;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Observability;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities;
using Fms.Inventory;
using Fms.Integration;
using Fms.Integration.Services;
using Fms.Purchasing;
using Fms.Bookings;
using Fms.Bookings.Services;
using Fms.Vendors;
using Fms.Vendors.Services;
using Fms.Work;
using Fms.Work.Services;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Identity;
using Fms.Platform;
using Fms.Platform.Outbox;

var builder = Host.CreateApplicationBuilder(args);

builder.AddFmsObservability("fms-worker");

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
// Background jobs act for no one: services that ask who is signed in see nobody (and so cannot be misused as someone).
builder.Services.AddSingleton<ICurrentUser, BackgroundUser>();
builder.Services.AddFmsDeployment(o => builder.Configuration.GetSection(DeploymentOptions.SectionName).Bind(o));
builder.Services.AddFmsStorage(builder.Configuration);
builder.Services.AddFmsPlatform(connectionString);
builder.Services.AddFmsFacilities(connectionString);
builder.Services.AddFmsWork(connectionString);
builder.Services.AddFmsInventory(connectionString);
builder.Services.AddFmsVendors(connectionString);
builder.Services.AddFmsPurchasing(connectionString);
builder.Services.AddFmsIntegration(connectionString);
builder.Services.AddFmsBookings(connectionString, builder.Configuration);
builder.Services.AddOptions<OutboxOptions>().Bind(builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.AddFmsEmail(builder.Configuration);
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<SlaMonitorWorker>();
builder.Services.AddHostedService<PmWorker>();
builder.Services.AddHostedService<ContractWorker>();
builder.Services.AddHostedService<ErpSyncWorker>();
builder.Services.AddHostedService<BookingWorker>();

// Module handlers register here as modules are built: builder.Services.AddOutboxHandler<SomeHandler>();

await builder.Build().RunAsync();

/// <summary>Polls the outbox. Busy when there is work, idle-waits when there is none; stops cleanly on shutdown.</summary>
internal sealed class OutboxWorker(OutboxProcessor processor, Microsoft.Extensions.Options.IOptions<OutboxOptions> options, ILogger<OutboxWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await processor.ProcessBatchAsync(stoppingToken);

                if (DateTimeOffset.UtcNow - lastPurge > TimeSpan.FromHours(1))
                {
                    lastPurge = DateTimeOffset.UtcNow;
                    var purged = await processor.PurgeAsync(stoppingToken);
                    if (purged > 0) log.LogInformation("Purged {Count} finished outbox messages", purged);
                }

                if (processed == 0) await Task.Delay(options.Value.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database briefly unavailable, etc.: keep the worker alive and try again.
                log.LogError(ex, "Outbox polling failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

/// <summary>Checks SLA clocks once a minute and raises alerts (see <see cref="SlaMonitor"/>).</summary>
internal sealed class SlaMonitorWorker(SlaMonitor monitor, ILogger<SlaMonitorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var raised = await monitor.RunOnceAsync(stoppingToken);
                if (raised > 0) log.LogInformation("Raised {Count} SLA alerts", raised);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "SLA monitoring failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}

internal sealed class BackgroundUser : ICurrentUser
{
    public bool IsAuthenticated => false;
    public string? Subject => null;
    public string? Provider => null;
    public Guid? TenantId => null;
    public Guid? UserId => null;
}

/// <summary>Creates preventive work orders when they fall due (see <see cref="PmGenerator"/>). Every few minutes is plenty: schedules are in days.</summary>
internal sealed class PmWorker(PmGenerator generator, ILogger<PmWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var created = await generator.RunOnceAsync(stoppingToken);
                if (created > 0) log.LogInformation("Created {Count} preventive work orders", created);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Preventive maintenance run failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}

/// <summary>Queues contract reminders (ending soon, ended). Contracts are dated in days, so a few times a day is plenty.</summary>
internal sealed class ContractWorker(ContractMonitor monitor, ILogger<ContractWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queued = await monitor.RunOnceAsync(stoppingToken);
                if (queued > 0) log.LogInformation("Queued {Count} contract reminders", queued);
                await Task.Delay(TimeSpan.FromHours(4), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Contract reminder run failed");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}

/// <summary>Sends queued changes to the accounting system. Every few seconds: a send is quick, and a person waiting to see a vendor appear in the ERP should not wait minutes.</summary>
internal sealed class ErpSyncWorker(ErpSyncRunner runner, ILogger<ErpSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await runner.RunOnceAsync(ct: stoppingToken);
                await Task.Delay(sent > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ERP sync run failed");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}

/// <summary>Frees bookings that needed a check-in and did not get one. Every minute: a room should not stay blocked long after its grace period.</summary>
internal sealed class BookingWorker(BookingMonitor monitor, ILogger<BookingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var freed = await monitor.RunOnceAsync(stoppingToken);
                if (freed > 0) log.LogInformation("Released {Count} bookings that were not checked in", freed);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Booking run failed");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
