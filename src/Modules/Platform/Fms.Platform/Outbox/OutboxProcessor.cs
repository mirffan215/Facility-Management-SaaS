using Fms.BuildingBlocks.Messaging;
using System.Diagnostics.Metrics;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fms.Platform.Outbox;

/// <summary>
/// Claims due messages with row locks (safe with many workers), runs each handler in its own tenant-scoped DI scope, and records
/// the outcome. Failures back off exponentially and end in a dead-letter state after <see cref="OutboxOptions.MaxAttempts"/>.
/// A worker that dies mid-message loses its lease and the message is picked up again.
/// </summary>
public static class OutboxMetrics
{
    public static readonly Meter Meter = new("Fms.Outbox");
    public static readonly Counter<long> Claimed = Meter.CreateCounter<long>("fms.outbox.claimed", description: "Messages claimed by a worker");
    public static readonly Counter<long> Succeeded = Meter.CreateCounter<long>("fms.outbox.succeeded", description: "Messages handled successfully");
    public static readonly Counter<long> Retried = Meter.CreateCounter<long>("fms.outbox.retried", description: "Failed attempts that will be retried");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("fms.outbox.dead_lettered", description: "Messages given up on");
}

public sealed class OutboxProcessor(
    IServiceScopeFactory scopes, IOptions<OutboxOptions> options, TimeProvider clock, ILogger<OutboxProcessor> log)
{
    private readonly string _workerId = Truncate($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}", 100);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[^max..];

    private sealed record Claimed(Guid Id, Guid TenantId, string Type, string PayloadJson, int Attempts);

    private sealed class ClaimedRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string Type { get; set; } = "";
        public string PayloadJson { get; set; } = "";
        public int Attempts { get; set; }
    }

    /// <summary>Processes up to one batch. Returns how many messages were claimed.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var batch = await ClaimAsync(ct);
        foreach (var message in batch)
        {
            await ProcessOneAsync(message, ct);
        }

        return batch.Count;
    }

    /// <summary>Deletes messages finished longer ago than the retention period. Dead letters are kept for inspection.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        await using var scope = PlatformScope(out var db);
        var cutoff = clock.GetUtcNow() - options.Value.Retention;
        return await db.OutboxMessages.Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private async Task<List<Claimed>> ClaimAsync(CancellationToken ct)
    {
        await using var scope = PlatformScope(out var db);
        var o = options.Value;
        var now = clock.GetUtcNow();
        var until = now + o.Lease;

        var rows = await db.Database.SqlQuery<ClaimedRow>($"""
            WITH due AS (
                SELECT TOP ({o.BatchSize}) *
                FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ProcessedAt IS NULL AND DeadLetteredAt IS NULL AND AvailableAt <= {now}
                  AND (LockedUntil IS NULL OR LockedUntil < {now})
                ORDER BY AvailableAt, CreatedAt)
            UPDATE due SET LockedUntil = {until}, LockedBy = {_workerId}, Attempts = Attempts + 1
            OUTPUT inserted.Id, inserted.TenantId, inserted.Type, inserted.PayloadJson, inserted.Attempts
            """).ToListAsync(ct);

        OutboxMetrics.Claimed.Add(rows.Count);
        return rows.Select(r => new Claimed(r.Id, r.TenantId, r.Type, r.PayloadJson, r.Attempts)).ToList();
    }

    private async Task ProcessOneAsync(Claimed message, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(message.TenantId);

            var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().FirstOrDefault(h => h.MessageType == message.Type)
                          ?? throw new InvalidOperationException($"No handler is registered for message type '{message.Type}'.");

            await handler.HandleAsync(new OutboxMessageContext(message.Id, message.TenantId, message.Type, message.PayloadJson, message.Attempts), ct);
            OutboxMetrics.Succeeded.Add(1, new KeyValuePair<string, object?>("type", message.Type));
            await MarkAsync(message.Id, m => m.SetProperty(x => x.ProcessedAt, clock.GetUtcNow()).SetProperty(x => x.LockedUntil, (DateTimeOffset?)null).SetProperty(x => x.LastError, (string?)null), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAsync(message, ex, ct);
        }
    }

    private async Task FailAsync(Claimed message, Exception ex, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var error = ex.ToString();
        error = error.Length > 2000 ? error[..2000] : error;

        if (message.Attempts >= o.MaxAttempts)
        {
            OutboxMetrics.DeadLettered.Add(1, new KeyValuePair<string, object?>("type", message.Type));
            log.LogError(ex, "Outbox message {MessageId} ({Type}) dead-lettered after {Attempts} attempts", message.Id, message.Type, message.Attempts);
            await MarkAsync(message.Id, m => m.SetProperty(x => x.DeadLetteredAt, now).SetProperty(x => x.LockedUntil, (DateTimeOffset?)null).SetProperty(x => x.LastError, error), ct);
            return;
        }

        var delay = TimeSpan.FromTicks(Math.Min(o.MaxRetryDelay.Ticks, o.BaseRetryDelay.Ticks * (1L << Math.Min(message.Attempts - 1, 20))));
        OutboxMetrics.Retried.Add(1, new KeyValuePair<string, object?>("type", message.Type));
        log.LogWarning(ex, "Outbox message {MessageId} ({Type}) failed on attempt {Attempts}; retrying in {Delay}", message.Id, message.Type, message.Attempts, delay);
        await MarkAsync(message.Id, m => m.SetProperty(x => x.AvailableAt, now + delay).SetProperty(x => x.LockedUntil, (DateTimeOffset?)null).SetProperty(x => x.LastError, error), ct);
    }

    private async Task MarkAsync(
        Guid id, Action<UpdateSettersBuilder<OutboxMessage>> set, CancellationToken ct)
    {
        await using var scope = PlatformScope(out var db);
        await db.OutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(set, ct);
    }

    private AsyncServiceScope PlatformScope(out PlatformDbContext db)
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
        db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return scope;
    }
}
