using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Fms.UnitTests;

public sealed class TenantIsolationTests
{
    private sealed class Widget : ITenantOwned
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class TestDb(DbContextOptions options, ITenantContext tenant) : TenantDbContext(options, tenant)
    {
        public DbSet<Widget> Widgets => Set<Widget>();
    }

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    private static DbContextOptions Options(string name) =>
        new DbContextOptionsBuilder<TestDb>().UseInMemoryDatabase(name).Options;

    private static TestDb Db(string name, Guid? tenantId, bool platform = false)
    {
        var ctx = new TenantContext();
        if (tenantId is { } t) ctx.SetTenant(t);
        if (platform) ctx.BeginPlatformScope();
        return new TestDb(Options(name), ctx);
    }

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_rows()
    {
        var name = nameof(Tenant_cannot_read_another_tenants_rows);
        await using (var a = Db(name, TenantA))
        {
            a.Widgets.Add(new Widget { Name = "a-secret" });
            await a.SaveChangesAsync();
        }

        await using var b = Db(name, TenantB);
        Assert.Empty(await b.Widgets.ToListAsync());

        await using var a2 = Db(name, TenantA);
        Assert.Single(await a2.Widgets.ToListAsync());
    }

    [Fact]
    public async Task Unresolved_tenant_sees_nothing()
    {
        var name = nameof(Unresolved_tenant_sees_nothing);
        await using (var a = Db(name, TenantA))
        {
            a.Widgets.Add(new Widget());
            await a.SaveChangesAsync();
        }

        await using var none = Db(name, null);
        Assert.Empty(await none.Widgets.ToListAsync());
    }

    [Fact]
    public async Task Writing_a_row_for_another_tenant_is_refused()
    {
        await using var a = Db(nameof(Writing_a_row_for_another_tenant_is_refused), TenantA);
        a.Widgets.Add(new Widget { TenantId = TenantB });
        await Assert.ThrowsAsync<TenantViolationException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task Saving_without_a_tenant_is_refused()
    {
        await using var none = Db(nameof(Saving_without_a_tenant_is_refused), null);
        none.Widgets.Add(new Widget());
        await Assert.ThrowsAsync<TenantRequiredException>(() => none.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_id_cannot_be_changed_on_an_existing_row()
    {
        var name = nameof(Tenant_id_cannot_be_changed_on_an_existing_row);
        await using var a = Db(name, TenantA);
        var w = new Widget();
        a.Widgets.Add(w);
        await a.SaveChangesAsync();

        w.TenantId = TenantB;
        await Assert.ThrowsAsync<TenantViolationException>(() => a.SaveChangesAsync());
    }

    [Fact]
    public async Task Platform_scope_sees_all_tenants_but_must_set_tenant_on_writes()
    {
        var name = nameof(Platform_scope_sees_all_tenants_but_must_set_tenant_on_writes);
        await using (var a = Db(name, TenantA)) { a.Widgets.Add(new Widget()); await a.SaveChangesAsync(); }
        await using (var b = Db(name, TenantB)) { b.Widgets.Add(new Widget()); await b.SaveChangesAsync(); }

        await using var platform = Db(name, null, platform: true);
        Assert.Equal(2, await platform.Widgets.CountAsync());

        platform.Widgets.Add(new Widget());
        await Assert.ThrowsAsync<TenantViolationException>(() => platform.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_context_cannot_switch_tenant()
    {
        var ctx = new TenantContext();
        ctx.SetTenant(TenantA);
        Assert.Throws<TenantViolationException>(() => ctx.SetTenant(TenantB));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Audit_events_are_append_only()
    {
        var name = nameof(Audit_events_are_append_only);
        await using var a = Db(name, TenantA);
        var evt = new AuditEvent { Action = "created", EntityType = "Widget", OccurredAt = DateTimeOffset.UtcNow };
        a.AuditEvents.Add(evt);
        await a.SaveChangesAsync();

        evt.Action = "tampered";
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => a.SaveChangesAsync());
        a.ChangeTracker.Clear();

        a.AuditEvents.Remove((await a.AuditEvents.ToListAsync()).Single());
        await Assert.ThrowsAsync<AppendOnlyViolationException>(() => a.SaveChangesAsync());
    }
}
