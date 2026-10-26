using System.Net;
using System.Net.Sockets;
using System.Text;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform;
using Fms.Platform.Domain;
using Fms.Platform.Notifications;
using Fms.Platform.Outbox;
using Fms.Platform.Persistence;
using Fms.Platform.Services;
using Fms.Work;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

/// <summary>SLA monitor, lifecycle events, in-app notices and email, end to end through the real outbox, on a database of its own.</summary>
[Collection(SqlCollection.Name)]
public sealed class NotificationPipelineTests(SqlServerFixture sql)
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingEmail : IEmailSender
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<EmailMessage> Sent = new();
        public int FailNext;

        public Task SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (Interlocked.Decrement(ref FailNext) >= 0) throw new InvalidOperationException("mail server unavailable");
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed class NoUser : ICurrentUser
    {
        public bool IsAuthenticated => false;
        public string? Subject => null;
        public string? Provider => null;
        public Guid? TenantId => null;
        public Guid? UserId => null;
    }

    private sealed record Rig(string Connection, ServiceProvider Services, ManualClock Clock, RecordingEmail Email, Guid Requester, Guid Dispatcher, Guid Tech, Guid OtherTenantManager)
    {
        public OutboxProcessor Processor => Services.GetRequiredService<OutboxProcessor>();

        public async Task DrainAsync()
        {
            for (var i = 0; i < 20 && await Processor.ProcessBatchAsync() > 0; i++) { }
        }

        public async Task<T> InTenantAsync<T>(Guid tenant, Func<IServiceProvider, Task<T>> work)
        {
            await using var scope = Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
            return await work(scope.ServiceProvider);
        }

        public Task<List<Notification>> NoticesAsync(Guid tenant, Guid user) =>
            InTenantAsync(tenant, sp => sp.GetRequiredService<PlatformDbContext>().Notifications.AsNoTracking().Where(n => n.UserId == user).OrderBy(n => n.CreatedAt).ToListAsync());

        public async Task<Guid> AddOrderAsync(Action<WorkOrder> shape)
        {
            return await InTenantAsync(TenantA, async sp =>
            {
                var wo = new WorkOrder { Number = Random.Shared.Next(1, 1_000_000), Title = "Chiller warm", LocationId = Guid.NewGuid(), RequestedBy = Requester, AssignedTo = Tech, Status = WorkOrderStatus.Assigned, RequestedAt = Clock.GetUtcNow(), UpdatedAt = Clock.GetUtcNow() };
                shape(wo);
                var db = sp.GetRequiredService<WorkDbContext>();
                db.WorkOrders.Add(wo);
                await db.SaveChangesAsync();
                return wo.Id;
            });
        }
    }

    private async Task<Rig> StartAsync()
    {
        var connection = await sql.CreateIsolatedDatabaseAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var email = new RecordingEmail();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<ICurrentUser>(new NoUser());
        services.AddSingleton<IEmailSender>(email);
        services.AddFmsPlatform(connection);
        services.AddFmsWork(connection);
        services.AddOptions<OutboxOptions>().Configure(o => { o.BaseRetryDelay = TimeSpan.FromSeconds(10); o.MaxAttempts = 5; });
        var provider = services.BuildServiceProvider();

        Guid requester, dispatcher, tech, other;
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await SystemRoleSync.EnsureAsync(db, TenantA);
            await SystemRoleSync.EnsureAsync(db, TenantB);
            requester = await AddUserAsync(db, TenantA, "requester", Fms.Platform.Security.SystemRoles.Requester);
            dispatcher = await AddUserAsync(db, TenantA, "dispatcher", Fms.Platform.Security.SystemRoles.FacilityManager);
            tech = await AddUserAsync(db, TenantA, "tech", Fms.Platform.Security.SystemRoles.Technician);
            other = await AddUserAsync(db, TenantB, "other-tenant-manager", Fms.Platform.Security.SystemRoles.FacilityManager);
        }

        return new Rig(connection, provider, clock, email, requester, dispatcher, tech, other);
    }

    private static async Task<Guid> AddUserAsync(PlatformDbContext db, Guid tenant, string name, string role)
    {
        var user = new User { TenantId = tenant, Provider = "entra", Subject = name + Guid.NewGuid().ToString("N")[..6], Email = $"{name}@example.test", DisplayName = name, CreatedAt = DateTimeOffset.UtcNow, LastSignInAt = DateTimeOffset.UtcNow };
        db.Users.Add(user);
        var roleId = await db.Roles.IgnoreQueryFilters().Where(r => r.TenantId == tenant && r.Name == role).Select(r => r.Id).SingleAsync();
        db.UserRoles.Add(new UserRole { TenantId = tenant, UserId = user.Id, RoleId = roleId, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task The_monitor_raises_each_sla_alert_once_and_tells_dispatchers_and_the_assignee_by_notice_and_email()
    {
        var rig = await StartAsync();
        var now = rig.Clock.GetUtcNow();
        // Approved 3 hours ago, nobody has started: the 1 hour response target is missed, and 3 of the 4 hours to resolve are used up.
        var id = await rig.AddOrderAsync(w =>
        {
            w.SlaStartedAt = now.AddHours(-3);
            w.ResolutionWindowStartAt = now.AddHours(-3);
            w.ResponseDueAt = now.AddHours(-2);
            w.ResolutionDueAt = now.AddHours(1);
        });
        var monitor = rig.Services.GetRequiredService<SlaMonitor>();

        Assert.Equal(2, await monitor.RunOnceAsync()); // response breached + resolution at risk
        Assert.Equal(0, await monitor.RunOnceAsync()); // nothing is raised twice

        rig.Clock.Advance(TimeSpan.FromHours(2)); // the resolution target passes too
        Assert.Equal(1, await monitor.RunOnceAsync());
        Assert.Equal(0, await monitor.RunOnceAsync());

        await rig.DrainAsync();

        var dispatcher = await rig.NoticesAsync(TenantA, rig.Dispatcher);
        var tech = await rig.NoticesAsync(TenantA, rig.Tech);
        Assert.Equal(["workorder.sla.ResolutionAtRisk", "workorder.sla.ResolutionBreached", "workorder.sla.ResponseBreached"], dispatcher.Select(n => n.Type).Order().ToArray());
        Assert.Equal(dispatcher.Select(n => n.Type).Order(), tech.Select(n => n.Type).Order());
        Assert.Empty(await rig.NoticesAsync(TenantA, rig.Requester)); // the requester is not told about internal deadlines
        Assert.Empty(await rig.NoticesAsync(TenantB, rig.OtherTenantManager));
        Assert.All(dispatcher, n => Assert.Equal($"/work-orders/{id}", n.LinkPath));
        Assert.All(dispatcher, n => Assert.Null(n.ReadAt));

        Assert.Equal(6, rig.Email.Sent.Count);
        Assert.Equal(["dispatcher@example.test", "tech@example.test"], rig.Email.Sent.Select(m => m.To).Distinct().Order().ToArray());
        Assert.All(rig.Email.Sent, m => { Assert.Contains("Chiller warm", m.Body); Assert.Matches("[؀-ۿ]", m.Body); });

        // Completed work is never alerted on.
        var done = await rig.AddOrderAsync(w => { w.SlaStartedAt = now.AddDays(-9); w.ResolutionWindowStartAt = now.AddDays(-9); w.ResponseDueAt = now.AddDays(-9); w.ResolutionDueAt = now.AddDays(-8); w.CompletedAt = now.AddDays(-8); w.Status = WorkOrderStatus.Completed; });
        Assert.NotEqual(Guid.Empty, done);
        Assert.Equal(0, await monitor.RunOnceAsync());
    }

    [Fact]
    public async Task Work_on_hold_raises_no_resolution_alerts()
    {
        var rig = await StartAsync();
        var now = rig.Clock.GetUtcNow();
        await rig.AddOrderAsync(w =>
        {
            w.Status = WorkOrderStatus.OnHold;
            w.RespondedAt = now.AddHours(-5);
            w.SlaStartedAt = now.AddHours(-10);
            w.ResolutionWindowStartAt = now.AddHours(-10);
            w.ResponseDueAt = now.AddHours(-9);
            w.ResolutionDueAt = now.AddHours(-1); // would be breached, but the clock is paused
            w.SlaPausedAt = now.AddHours(-4);
            w.ResolutionRemainingMinutes = 30;
        });
        Assert.Equal(0, await rig.Services.GetRequiredService<SlaMonitor>().RunOnceAsync());
    }

    [Fact]
    public async Task Lifecycle_events_notify_the_right_people_once_even_if_delivered_twice()
    {
        var rig = await StartAsync();
        var id = await rig.AddOrderAsync(w => { w.Status = WorkOrderStatus.Requested; w.AssignedTo = null; });

        // The requester raised it: approvers (here the dispatcher, who holds the approve permission) are told; the requester is not.
        var messageId = Guid.NewGuid();
        var message = new OutboxMessageContext(messageId, TenantA, WorkMessages.Event,
            System.Text.Json.JsonSerializer.Serialize(new WorkOrderEventMessage(id, WorkOrderAction.Created, rig.Requester, null)), 1);
        for (var delivery = 0; delivery < 2; delivery++)
        {
            await rig.InTenantAsync(TenantA, async sp =>
            {
                await sp.GetServices<IOutboxHandler>().Single(h => h.MessageType == WorkMessages.Event).HandleAsync(message, CancellationToken.None);
                return 0;
            });
        }

        var notices = await rig.NoticesAsync(TenantA, rig.Dispatcher);
        var notice = Assert.Single(notices); // delivered twice, noticed once
        Assert.Equal("workorder.Created", notice.Type);
        Assert.Contains("\"number\"", notice.DataJson);
        Assert.Empty(await rig.NoticesAsync(TenantA, rig.Requester));
        Assert.Empty(await rig.NoticesAsync(TenantA, rig.Tech));

        await rig.DrainAsync();
        Assert.Single(rig.Email.Sent); // and mailed once
    }

    [Fact]
    public async Task A_mail_outage_is_retried_and_the_notice_is_never_lost_or_duplicated()
    {
        var rig = await StartAsync();
        var id = await rig.AddOrderAsync(w => { w.Status = WorkOrderStatus.Assigned; });
        rig.Email.FailNext = 2;

        await rig.InTenantAsync(TenantA, async sp =>
        {
            sp.GetRequiredService<IOutbox>().Enqueue(WorkMessages.Event, new WorkOrderEventMessage(id, WorkOrderAction.Assigned, rig.Dispatcher, null));
            await sp.GetRequiredService<WorkDbContext>().SaveChangesAsync();
            return 0;
        });

        await rig.DrainAsync(); // event handled, email attempt 1 fails
        Assert.Empty(rig.Email.Sent);
        Assert.Single(await rig.NoticesAsync(TenantA, rig.Tech)); // the in-app notice does not wait for email

        for (var i = 0; i < 4 && rig.Email.Sent.IsEmpty; i++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(5));
            await rig.DrainAsync();
        }

        Assert.Equal(["tech@example.test", "requester@example.test"], rig.Email.Sent.Select(m => m.To).ToArray().OrderByDescending(t => t.StartsWith("tech")).ToArray());
        Assert.Single(await rig.NoticesAsync(TenantA, rig.Tech));
    }

    [Fact]
    public async Task People_only_see_and_clear_their_own_notices()
    {
        var rig = await StartAsync();
        var id = await rig.AddOrderAsync(w => { });
        await rig.InTenantAsync(TenantA, async sp =>
        {
            sp.GetRequiredService<IOutbox>().Enqueue(WorkMessages.Event, new WorkOrderEventMessage(id, WorkOrderAction.Reopened, rig.Dispatcher, "still warm"));
            await sp.GetRequiredService<WorkDbContext>().SaveChangesAsync();
            return 0;
        });
        await rig.DrainAsync();

        var noticeId = Assert.Single(await rig.NoticesAsync(TenantA, rig.Tech)).Id;

        async Task<NotificationService> ServiceFor(Guid user, Guid tenant)
        {
            var sp = rig.Services.CreateAsyncScope().ServiceProvider;
            sp.GetRequiredService<TenantContext>().SetTenant(tenant);
            await Task.CompletedTask;
            return new NotificationService(sp.GetRequiredService<PlatformDbContext>(), new FixedUser(user, tenant), rig.Clock);
        }

        var mine = await ServiceFor(rig.Tech, TenantA);
        Assert.Equal(1, await mine.UnreadCountAsync());
        Assert.Equal(0, await (await ServiceFor(rig.Dispatcher, TenantA)).UnreadCountAsync() - 0); // the actor was not told
        Assert.False(await (await ServiceFor(rig.Requester, TenantA)).MarkReadAsync(noticeId)); // someone else's notice
        Assert.Equal(1, await mine.UnreadCountAsync());
        Assert.True(await mine.MarkReadAsync(noticeId));
        Assert.Equal(0, await mine.UnreadCountAsync());
        Assert.Single((await mine.ListAsync(false, null, null)).Items);
        Assert.Empty((await mine.ListAsync(true, null, null)).Items);
        Assert.Empty((await (await ServiceFor(rig.OtherTenantManager, TenantB)).ListAsync(false, null, null)).Items);
    }

    private sealed class FixedUser(Guid id, Guid tenant) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public string? Subject => "x";
        public string? Provider => "entra";
        public Guid? TenantId => tenant;
        public Guid? UserId => id;
    }

    // ---------------- SMTP ----------------

    /// <summary>A minimal SMTP server: enough of the protocol to accept one message and keep what was sent.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _loop;
        public string Received { get; private set; } = "";
        public string? From { get; private set; }
        public List<string> Recipients { get; } = [];
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeSmtpServer()
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 fake ready");
                var data = new StringBuilder();
                var inData = false;
                while (await reader.ReadLineAsync() is { } line)
                {
                    if (inData)
                    {
                        if (line == ".") { inData = false; Received = data.ToString(); await writer.WriteLineAsync("250 queued"); }
                        else data.AppendLine(line.StartsWith("..") ? line[1..] : line);
                        continue;
                    }

                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO" or "HELO": await writer.WriteLineAsync("250 fake"); break;
                        case "MAIL": From = line; await writer.WriteLineAsync("250 ok"); break;
                        case "RCPT": Recipients.Add(line); await writer.WriteLineAsync("250 ok"); break;
                        case "DATA": inData = true; await writer.WriteLineAsync("354 go"); break;
                        case "QUIT": await writer.WriteLineAsync("221 bye"); return;
                        default: await writer.WriteLineAsync("250 ok"); break;
                    }
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* the test already has what it needs */ }
        }
    }

    [Fact]
    public async Task Smtp_sender_delivers_a_bilingual_message_with_the_right_addresses()
    {
        await using var server = new FakeSmtpServer();
        var sender = new SmtpEmailSender(Options.Create(new EmailOptions { Provider = "Smtp", Host = "127.0.0.1", Port = server.Port, UseSsl = false, From = "fms@example.test", FromName = "FMS" }));

        await sender.SendAsync(new EmailMessage("tina@example.test", "WO-000007 was assigned", "WO-000007 was assigned\nLeak\n\nWO-000007 تم إسناده\nLeak\n"));

        Assert.Contains("fms@example.test", server.From);
        Assert.Contains("tina@example.test", Assert.Single(server.Recipients));
        Assert.Contains("WO-000007 was assigned", server.Received);
        Assert.Contains("charset=utf-8", server.Received, StringComparison.OrdinalIgnoreCase); // so the Arabic survives
    }

    [Fact]
    public void Email_configuration_is_validated_and_defaults_to_not_sending()
    {
        var validator = new EmailOptionsValidator();
        Assert.True(validator.Validate(null, new EmailOptions()).Succeeded);
        Assert.False(validator.Validate(null, new EmailOptions { Provider = "Smtp" }).Succeeded); // host and from missing
        Assert.False(validator.Validate(null, new EmailOptions { Provider = "Carrier pigeon" }).Succeeded);
        Assert.True(validator.Validate(null, new EmailOptions { Provider = "Smtp", Host = "smtp.example.test", From = "a@example.test" }).Succeeded);
    }
}
