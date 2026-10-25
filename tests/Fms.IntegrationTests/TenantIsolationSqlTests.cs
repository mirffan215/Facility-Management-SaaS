using Fms.BuildingBlocks.Audit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class TenantIsolationSqlTests(SqlServerFixture sql)
{
    private async Task<(Guid A, Guid B)> SeedUsersAsync()
    {
        var tag = Guid.NewGuid().ToString("N");
        await using (var a = sql.App(TenantA))
        {
            a.Users.Add(NewUser($"a-{tag}"));
            await a.SaveChangesAsync();
        }

        await using (var b = sql.App(TenantB))
        {
            b.Users.Add(NewUser($"b-{tag}"));
            await b.SaveChangesAsync();
        }

        return (TenantA, TenantB);
    }

    [Fact]
    public async Task Ef_filter_limits_a_tenant_to_its_own_rows()
    {
        await SeedUsersAsync();
        await using var a = sql.App(TenantA);

        Assert.All(await a.Users.ToListAsync(), u => Assert.Equal(TenantA, u.TenantId));
    }

    [Fact]
    public async Task Database_blocks_cross_tenant_reads_even_when_the_ef_filter_is_bypassed()
    {
        await SeedUsersAsync();
        await using var a = sql.App(TenantA);

        var rows = await a.Users.IgnoreQueryFilters().ToListAsync();

        Assert.NotEmpty(rows);
        Assert.All(rows, u => Assert.Equal(TenantA, u.TenantId));
    }

    [Fact]
    public async Task Database_blocks_cross_tenant_reads_in_raw_sql()
    {
        await SeedUsersAsync();
        await using var a = sql.App(TenantA);

        var foreign = await a.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.Users WHERE TenantId = {TenantB}").SingleAsync();

        Assert.Equal(0, foreign);
    }

    [Fact]
    public async Task No_tenant_means_no_rows()
    {
        await SeedUsersAsync();
        await using var none = sql.App(null);

        Assert.Equal(0, await none.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.Users").SingleAsync());
    }

    [Fact]
    public async Task Database_blocks_inserting_a_row_for_another_tenant()
    {
        await using var a = sql.App(TenantA);

        var ex = await Assert.ThrowsAsync<SqlException>(() => a.Database.ExecuteSqlAsync(
            $"INSERT INTO dbo.Users (Id, TenantId, Provider, Subject, IsActive, CreatedAt, LastSignInAt) VALUES ({Guid.NewGuid()}, {TenantB}, N'x', N'forged', 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())"));

        Assert.Equal(33504, ex.Number);
    }

    [Fact]
    public async Task Database_blocks_moving_a_row_to_another_tenant()
    {
        var tag = Guid.NewGuid().ToString("N");
        await using var a = sql.App(TenantA);
        a.Users.Add(NewUser($"mine-{tag}"));
        await a.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() => a.Database.ExecuteSqlAsync(
            $"UPDATE dbo.Users SET TenantId = {TenantB} WHERE Subject = {$"mine-{tag}"}"));

        Assert.Equal(33504, ex.Number);
    }

    [Fact]
    public async Task Another_tenants_rows_cannot_be_updated_or_deleted()
    {
        var tag = Guid.NewGuid().ToString("N");
        await using (var b = sql.App(TenantB))
        {
            b.Users.Add(NewUser($"victim-{tag}"));
            await b.SaveChangesAsync();
        }

        await using var a = sql.App(TenantA);
        Assert.Equal(0, await a.Database.ExecuteSqlAsync($"UPDATE dbo.Users SET DisplayName = N'pwned' WHERE Subject = {$"victim-{tag}"}"));
        Assert.Equal(0, await a.Database.ExecuteSqlAsync($"DELETE FROM dbo.Users WHERE Subject = {$"victim-{tag}"}"));

        await using var b2 = sql.App(TenantB);
        var victim = await b2.Users.SingleAsync(u => u.Subject == $"victim-{tag}");
        Assert.Null(victim.DisplayName);
    }

    [Fact]
    public async Task Platform_scope_sees_every_tenant()
    {
        await SeedUsersAsync();
        await using var platform = sql.App(null, platform: true);

        var tenants = await platform.Users.Select(u => u.TenantId).Distinct().ToListAsync();

        Assert.Contains(TenantA, tenants);
        Assert.Contains(TenantB, tenants);
    }

    [Fact]
    public async Task Pooled_connections_never_leak_one_tenants_context_into_the_next()
    {
        await SeedUsersAsync();
        for (var i = 0; i < 10; i++)
        {
            var tenant = i % 2 == 0 ? TenantA : TenantB;
            await using var db = sql.App(tenant);
            var tenants = await db.Database.SqlQuery<Guid>($"SELECT TenantId AS Value FROM dbo.Users").ToListAsync();

            Assert.NotEmpty(tenants);
            Assert.All(tenants, t => Assert.Equal(tenant, t));
        }
    }

    [Fact]
    public async Task Audit_events_can_be_written_but_never_changed_or_removed()
    {
        await using var a = sql.App(TenantA);
        var id = Guid.NewGuid();
        a.AuditEvents.Add(new AuditEvent { Id = id, Action = "created", EntityType = "Asset", OccurredAt = DateTimeOffset.UtcNow });
        await a.SaveChangesAsync();

        var update = await Assert.ThrowsAsync<SqlException>(() => a.Database.ExecuteSqlAsync($"UPDATE dbo.AuditEvents SET Action = N'tampered' WHERE Id = {id}"));
        var delete = await Assert.ThrowsAsync<SqlException>(() => a.Database.ExecuteSqlAsync($"DELETE FROM dbo.AuditEvents WHERE Id = {id}"));

        Assert.Equal(51000, update.Number);
        Assert.Equal(51000, delete.Number);
    }

    [Fact]
    public async Task Audit_events_are_tenant_isolated_in_the_database()
    {
        var id = Guid.NewGuid();
        await using (var a = sql.App(TenantA))
        {
            a.AuditEvents.Add(new AuditEvent { Id = id, Action = "created", EntityType = "Asset", OccurredAt = DateTimeOffset.UtcNow });
            await a.SaveChangesAsync();
        }

        await using var b = sql.App(TenantB);
        Assert.False(await b.AuditEvents.IgnoreQueryFilters().AnyAsync(e => e.Id == id));
    }

    [Fact]
    public async Task The_application_login_cannot_disable_the_policy_or_drop_the_trigger()
    {
        await using var a = sql.App(TenantA);

        await Assert.ThrowsAsync<SqlException>(() => a.Database.ExecuteSqlRawAsync("ALTER SECURITY POLICY security.TenantIsolation WITH (STATE = OFF)"));
        await Assert.ThrowsAsync<SqlException>(() => a.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_AuditEvents_AppendOnly"));
        await Assert.ThrowsAsync<SqlException>(() => a.Database.ExecuteSqlRawAsync("DROP SECURITY POLICY security.TenantIsolation"));
    }

    [Fact]
    public async Task A_user_is_unique_per_tenant_and_provider_subject_but_may_exist_in_two_tenants()
    {
        var subject = "dup-" + Guid.NewGuid().ToString("N");
        await using (var a = sql.App(TenantA))
        {
            a.Users.Add(NewUser(subject));
            await a.SaveChangesAsync();
        }

        await using (var b = sql.App(TenantB))
        {
            b.Users.Add(NewUser(subject));
            await b.SaveChangesAsync();
        }

        await using var again = sql.App(TenantA);
        again.Users.Add(NewUser(subject));
        await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync());
    }
}
