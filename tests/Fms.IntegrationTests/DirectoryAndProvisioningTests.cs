using Fms.BuildingBlocks.Deployment;
using Fms.Platform.Domain;
using Fms.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class DirectoryAndProvisioningTests(SqlServerFixture sql)
{
    private static IOptions<DeploymentOptions> Saas() => Options.Create(new DeploymentOptions { Mode = DeploymentMode.SaaS });

    private async Task<(Guid Tenant, string External)> NewMappedTenantAsync(bool active = true)
    {
        var external = Guid.NewGuid().ToString();
        var tenant = new Tenant { Name = "T", IsActive = active, CreatedAt = DateTimeOffset.UtcNow };
        await using var platform = sql.App(null, platform: true);
        platform.Tenants.Add(tenant);
        await platform.SaveChangesAsync();
        platform.TenantIdentityMappings.Add(new TenantIdentityMapping { TenantId = tenant.Id, Provider = "entra", ExternalTenantId = external });
        await platform.SaveChangesAsync();
        return (tenant.Id, external);
    }

    [Fact]
    public async Task Saas_directory_resolves_a_stored_mapping()
    {
        var (tenant, external) = await NewMappedTenantAsync();
        await using var db = sql.App(null);

        Assert.Equal(tenant, await new DbTenantDirectory(db, Saas()).ResolveAsync("entra", external));
    }

    [Fact]
    public async Task Saas_directory_does_not_resolve_unknown_wrong_provider_or_inactive_tenants()
    {
        var (_, external) = await NewMappedTenantAsync();
        var (_, inactiveExternal) = await NewMappedTenantAsync(active: false);
        await using var db = sql.App(null);
        var directory = new DbTenantDirectory(db, Saas());

        Assert.Null(await directory.ResolveAsync("entra", Guid.NewGuid().ToString()));
        Assert.Null(await directory.ResolveAsync("google", external));
        Assert.Null(await directory.ResolveAsync("entra", inactiveExternal));
        Assert.Null(await directory.ResolveAsync("entra", null));
    }

    [Fact]
    public async Task A_provider_tenant_can_map_to_only_one_fms_tenant()
    {
        var (_, external) = await NewMappedTenantAsync();
        await using var platform = sql.App(null, platform: true);
        platform.TenantIdentityMappings.Add(new TenantIdentityMapping { TenantId = TenantA, Provider = "entra", ExternalTenantId = external });

        await Assert.ThrowsAsync<DbUpdateException>(() => platform.SaveChangesAsync());
    }

    [Fact]
    public async Task OnPrem_directory_resolves_the_single_existing_active_tenant_only()
    {
        await using var db = sql.App(null);
        IOptions<DeploymentOptions> OnPrem(Guid id) => Options.Create(new DeploymentOptions { Mode = DeploymentMode.OnPrem, SingleTenantId = id });

        Assert.Equal(TenantA, await new DbTenantDirectory(db, OnPrem(TenantA)).ResolveAsync("any", null));
        Assert.Null(await new DbTenantDirectory(db, OnPrem(Guid.NewGuid())).ResolveAsync("any", null));
    }

    [Fact]
    public async Task First_sign_in_creates_the_user_and_later_sign_ins_reuse_it()
    {
        var subject = "s-" + Guid.NewGuid().ToString("N");
        await using var db1 = sql.App(TenantA);
        var first = await new UserProvisioner(db1, TimeProvider.System).EnsureAsync("entra", subject, "a@x.test", "Ann");

        await using var db2 = sql.App(TenantA);
        var second = await new UserProvisioner(db2, TimeProvider.System).EnsureAsync("entra", subject, "a@x.test", "Ann");

        Assert.Equal(first.UserId, second.UserId);
        Assert.True(first.IsActive);
    }

    [Fact]
    public async Task The_same_person_gets_a_separate_user_in_each_tenant()
    {
        var subject = "s-" + Guid.NewGuid().ToString("N");
        await using var a = sql.App(TenantA);
        await using var b = sql.App(TenantB);

        var inA = await new UserProvisioner(a, TimeProvider.System).EnsureAsync("entra", subject, null, null);
        var inB = await new UserProvisioner(b, TimeProvider.System).EnsureAsync("entra", subject, null, null);

        Assert.NotEqual(inA.UserId, inB.UserId);
    }

    [Fact]
    public async Task Concurrent_first_sign_ins_create_exactly_one_user()
    {
        var subject = "race-" + Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = sql.App(TenantA);
            return await new UserProvisioner(db, TimeProvider.System).EnsureAsync("entra", subject, null, null);
        }));

        Assert.Single(results.Select(r => r.UserId).Distinct());
        await using var check = sql.App(TenantA);
        Assert.Equal(1, await check.Users.CountAsync(u => u.Subject == subject));
    }

    [Fact]
    public async Task Deactivated_users_are_reported_inactive()
    {
        var subject = "off-" + Guid.NewGuid().ToString("N");
        await using (var db = sql.App(TenantA))
        {
            var u = NewUser(subject);
            u.IsActive = false;
            db.Users.Add(u);
            await db.SaveChangesAsync();
        }

        await using var again = sql.App(TenantA);
        Assert.False((await new UserProvisioner(again, TimeProvider.System).EnsureAsync("entra", subject, null, null)).IsActive);
    }
}
