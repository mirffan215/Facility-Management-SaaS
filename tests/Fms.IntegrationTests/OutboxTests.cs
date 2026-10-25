using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform;
using Fms.Platform.Domain;
using Fms.Platform.Outbox;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class OutboxTests(SqlServerFixture sql)
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Records what each message handler call saw. Behaviour is scripted per test.</summary>
    private sealed class Probe
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<(Guid MessageId, Guid? Tenant, int Attempt)> Calls = [];
        public Func<OutboxMessageContext, Task>? Behaviour { get; set; }
    }

    private sealed class ProbeHandler(Probe probe, ITenantContext tenant) : IOutboxHandler
    {
        public string MessageType => "test.message";

        public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
        {
            probe.Calls.Add((message.MessageId, tenant.TenantId, message.Attempt));
            if (probe.Behaviour is { } behaviour) await behaviour(message);
        }
    }

    private sealed record Harness(string ConnectionString, ServiceProvider Services, ManualClock Clock, Probe Probe, OutboxProcessor Processor)
    {
        public PlatformDbContext Db(Guid? tenant, bool platform = false)
        {
            var ctx = new TenantContext();
            if (tenant is { } t) ctx.SetTenant(t);
            if (platform) ctx.BeginPlatformScope();
            return new PlatformDbContext(
                new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(ConnectionString)
                    .AddInterceptors(new Fms.BuildingBlocks.Persistence.TenantSessionContextInterceptor(ctx)).Options, ctx);
        }

        public async Task<Guid> SendAsync(Guid tenant, object? payload = null, string type = "test.message")
        {
            await using var scope = Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
            scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(type, payload ?? new { hello = "world" });
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.SaveChangesAsync();
            return db.OutboxMessages.Local.Single().Id;
        }

        public async Task<OutboxMessage> GetAsync(Guid id)
        {
            await using var db = Db(null, platform: true);
            return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
        }
    }

    private async Task<Harness> StartAsync(Action<OutboxOptions>? configure = null)
    {
        var connection = await sql.CreateIsolatedDatabaseAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var probe = new Probe();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<Fms.BuildingBlocks.Identity.ICurrentUser>(new NoUser());
        services.AddFmsPlatform(connection);
        services.AddSingleton(probe);
        services.AddOutboxHandler<ProbeHandler>();
        services.AddOptions<OutboxOptions>().Configure(o =>
        {
            o.BaseRetryDelay = TimeSpan.FromSeconds(10);
            o.MaxAttempts = 3;
            o.Lease = TimeSpan.FromMinutes(5);
            configure?.Invoke(o);
        });
        // TimeProvider.System is registered by AddFmsPlatform via TryAdd; ours was added first and wins.

        var provider = services.BuildServiceProvider();
        return new Harness(connection, provider, clock, probe, provider.GetRequiredService<OutboxProcessor>());
    }

    private sealed class NoUser : Fms.BuildingBlocks.Identity.ICurrentUser
    {
        public bool IsAuthenticated => false;
        public string? Subject => null;
        public string? Provider => null;
        public Guid? TenantId => null;
        public Guid? UserId => null;
    }

    [Fact]
    public async Task A_message_is_delivered_once_in_the_senders_tenant()
    {
        var h = await StartAsync();
        var id = await h.SendAsync(TenantA);

        Assert.Equal(1, await h.Processor.ProcessBatchAsync());
        Assert.Equal(0, await h.Processor.ProcessBatchAsync());

        var call = Assert.Single(h.Probe.Calls);
        Assert.Equal((id, (Guid?)TenantA, 1), call);
        Assert.NotNull((await h.GetAsync(id)).ProcessedAt);
    }

    [Fact]
    public async Task A_message_is_stored_only_if_the_business_change_commits()
    {
        var h = await StartAsync();
        var subject = "dup";
        await using (var first = h.Db(TenantA)) { first.Users.Add(NewUser(subject)); await first.SaveChangesAsync(); }

        await using (var failing = h.Db(TenantA))
        {
            failing.Users.Add(NewUser(subject));
            failing.OutboxMessages.Add(new OutboxMessage { Type = "test.message", CreatedAt = DateTimeOffset.UtcNow, AvailableAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => failing.SaveChangesAsync());
        }

        await using var check = h.Db(null, platform: true);
        Assert.Equal(0, await check.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task A_failed_message_is_retried_after_a_backoff_and_then_succeeds()
    {
        var h = await StartAsync();
        h.Probe.Behaviour = m => m.Attempt < 2 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        var id = await h.SendAsync(TenantA);

        Assert.Equal(1, await h.Processor.ProcessBatchAsync());
        Assert.Equal(0, await h.Processor.ProcessBatchAsync());
        var failed = await h.GetAsync(id);
        Assert.Null(failed.ProcessedAt);
        Assert.Contains("boom", failed.LastError);

        h.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, await h.Processor.ProcessBatchAsync());

        var done = await h.GetAsync(id);
        Assert.NotNull(done.ProcessedAt);
        Assert.Equal(2, done.Attempts);
        Assert.Null(done.LastError);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_and_left_alone()
    {
        var h = await StartAsync();
        h.Probe.Behaviour = _ => throw new InvalidOperationException("always");
        var id = await h.SendAsync(TenantA);

        for (var i = 0; i < 3; i++)
        {
            await h.Processor.ProcessBatchAsync();
            h.Clock.Advance(TimeSpan.FromHours(1));
        }

        var dead = await h.GetAsync(id);
        Assert.NotNull(dead.DeadLetteredAt);
        Assert.Equal(3, dead.Attempts);
        Assert.Equal(0, await h.Processor.ProcessBatchAsync());
        Assert.Equal(3, h.Probe.Calls.Count);
    }

    [Fact]
    public async Task A_message_with_no_handler_is_retried_then_dead_lettered()
    {
        var h = await StartAsync(o => o.MaxAttempts = 2);
        var id = await h.SendAsync(TenantA, type: "nobody.handles.this");

        for (var i = 0; i < 2; i++)
        {
            await h.Processor.ProcessBatchAsync();
            h.Clock.Advance(TimeSpan.FromHours(1));
        }

        var dead = await h.GetAsync(id);
        Assert.NotNull(dead.DeadLetteredAt);
        Assert.Contains("No handler", dead.LastError);
    }

    [Fact]
    public async Task Parallel_workers_deliver_every_message_exactly_once()
    {
        var h = await StartAsync(o => o.BatchSize = 5);
        var sent = new List<Guid>();
        for (var i = 0; i < 40; i++) sent.Add(await h.SendAsync(i % 2 == 0 ? TenantA : TenantB));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (await h.Processor.ProcessBatchAsync() > 0) { }
        })));

        Assert.Equal(40, h.Probe.Calls.Count);
        Assert.Equal(sent.Order(), h.Probe.Calls.Select(c => c.MessageId).Order());
        Assert.All(h.Probe.Calls, c => Assert.Equal(1, c.Attempt));
    }

    [Fact]
    public async Task Each_message_runs_in_its_own_tenant()
    {
        var h = await StartAsync();
        var a = await h.SendAsync(TenantA);
        var b = await h.SendAsync(TenantB);

        while (await h.Processor.ProcessBatchAsync() > 0) { }

        Assert.Equal(TenantA, h.Probe.Calls.Single(c => c.MessageId == a).Tenant);
        Assert.Equal(TenantB, h.Probe.Calls.Single(c => c.MessageId == b).Tenant);
    }

    [Fact]
    public async Task A_message_held_by_a_crashed_worker_is_picked_up_after_the_lease_expires()
    {
        var h = await StartAsync();
        var id = await h.SendAsync(TenantA);
        await using (var db = h.Db(null, platform: true))
        {
            // Simulates a worker that claimed the message and died before finishing.
            await db.OutboxMessages.Where(m => m.Id == id).ExecuteUpdateAsync(s =>
                s.SetProperty(m => m.LockedUntil, h.Clock.GetUtcNow().AddMinutes(5)).SetProperty(m => m.Attempts, 1));
        }

        Assert.Equal(0, await h.Processor.ProcessBatchAsync());
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await h.Processor.ProcessBatchAsync());
        Assert.Equal(2, Assert.Single(h.Probe.Calls).Attempt);
    }

    [Fact]
    public async Task Outbox_rows_are_tenant_isolated_in_the_database()
    {
        var h = await StartAsync();
        await h.SendAsync(TenantA);
        await h.SendAsync(TenantB);

        await using var a = h.Db(TenantA);
        Assert.Equal(1, await a.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.OutboxMessages").SingleAsync());
        Assert.Equal(0, await a.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.OutboxMessages WHERE TenantId = {TenantB}").SingleAsync());
    }

    [Fact]
    public async Task Purge_removes_old_finished_messages_but_keeps_pending_and_dead_letters()
    {
        var h = await StartAsync(o => o.MaxAttempts = 1);
        var done = await h.SendAsync(TenantA);
        await h.Processor.ProcessBatchAsync();

        h.Probe.Behaviour = _ => throw new InvalidOperationException("x");
        var dead = await h.SendAsync(TenantA);
        await h.Processor.ProcessBatchAsync();
        h.Probe.Behaviour = null;
        var pending = await h.SendAsync(TenantA);

        h.Clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(1, await h.Processor.PurgeAsync());

        await using var check = h.Db(null, platform: true);
        var remaining = await check.OutboxMessages.Select(m => m.Id).ToListAsync();
        Assert.DoesNotContain(done, remaining);
        Assert.Contains(dead, remaining);
        Assert.Contains(pending, remaining);
    }
}

[Collection(SqlCollection.Name)]
public sealed class OutboxMetricsTests(SqlServerFixture sql)
{
    [Fact]
    public async Task Outbox_publishes_claim_success_retry_and_dead_letter_counts()
    {
        var totals = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == "Fms.Outbox") l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => totals.AddOrUpdate(instrument.Name, value, (_, old) => old + value));
        listener.Start();

        var connection = await sql.CreateIsolatedDatabaseAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<Fms.BuildingBlocks.Identity.ICurrentUser, NoUser>();
        services.AddFmsPlatform(connection);
        services.AddSingleton<Behaviour>();
        services.AddOutboxHandler<ScriptedHandler>();
        services.AddOptions<OutboxOptions>().Configure(o => { o.MaxAttempts = 2; o.BaseRetryDelay = TimeSpan.Zero; });
        await using var provider = services.BuildServiceProvider();
        var processor = provider.GetRequiredService<OutboxProcessor>();
        var behaviour = provider.GetRequiredService<Behaviour>();

        async Task SendAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(TenantA);
            scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue("scripted", new { });
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync();
        }

        await SendAsync();
        await processor.ProcessBatchAsync();                   // success
        behaviour.Fail = true;
        await SendAsync();
        await processor.ProcessBatchAsync();                   // attempt 1 fails: retry
        await processor.ProcessBatchAsync();                   // attempt 2 fails: dead letter
        listener.RecordObservableInstruments();

        Assert.Equal(1, totals["fms.outbox.succeeded"]);
        Assert.Equal(1, totals["fms.outbox.retried"]);
        Assert.Equal(1, totals["fms.outbox.dead_lettered"]);
        Assert.Equal(3, totals["fms.outbox.claimed"]);
    }

    private sealed class Behaviour { public bool Fail { get; set; } }

    private sealed class ScriptedHandler(Behaviour behaviour) : IOutboxHandler
    {
        public string MessageType => "scripted";
        public Task HandleAsync(OutboxMessageContext message, CancellationToken ct) =>
            behaviour.Fail ? throw new InvalidOperationException("scripted failure") : Task.CompletedTask;
    }

    private sealed class NoUser : Fms.BuildingBlocks.Identity.ICurrentUser
    {
        public bool IsAuthenticated => false;
        public string? Subject => null;
        public string? Provider => null;
        public Guid? TenantId => null;
        public Guid? UserId => null;
    }
}
