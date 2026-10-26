using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Cloud;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Observability;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities;
using Fms.Work;
using Fms.Work.Services;
using Fms.BuildingBlocks.Notifications;
using Fms.Platform;
using Fms.Platform.Outbox;

var builder = Host.CreateApplicationBuilder(args);

builder.AddFmsObservability("fms-worker");

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is required.");

builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddFmsDeployment(o => builder.Configuration.GetSection(DeploymentOptions.SectionName).Bind(o));
builder.Services.AddFmsStorage(builder.Configuration);
builder.Services.AddFmsPlatform(connectionString);
builder.Services.AddFmsFacilities(connectionString);
builder.Services.AddFmsWork(connectionString);
builder.Services.AddOptions<OutboxOptions>().Bind(builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.AddFmsEmail(builder.Configuration);
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<SlaMonitorWorker>();

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
