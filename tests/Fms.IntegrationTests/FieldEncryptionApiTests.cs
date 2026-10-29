using System.Net;
using System.Security.Cryptography;
using Fms.Api;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Visitors.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

/// <summary>Contact details of visitors are unreadable in the database when the operator has configured a key, and read back as ever through the API.</summary>
[Collection(SqlCollection.Name)]
public sealed class FieldEncryptionApiTests(SqlServerFixture sql) : IDisposable
{
    private static readonly string Key1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string Key2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private ApiHarness? _api;

    public void Dispose()
    {
        _api?.Dispose();
        FieldEncryption.Configure(new FieldEncryptionOptions()); // leave the process as other tests expect it
    }

    private ApiHarness Start(params (string Id, string Key)[] keys)
    {
        var settings = new Dictionary<string, string?> { ["Encryption:CurrentKeyId"] = keys[^1].Id };
        foreach (var (id, key) in keys) settings[$"Encryption:Keys:{id}"] = key;
        _api?.Dispose();
        return _api = new ApiHarness(sql, settings: settings);
    }

    private async Task<(Guid Tenant, HttpClient Host, HttpClient Reception, LocationDto Site)> NewRigAsync(ApiHarness api)
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Enc " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return (tenant, await api.ClientAsync(tenant, SystemRoles.Requester), await api.ClientAsync(tenant, SystemRoles.Reception), site);
    }

    private static async Task<VisitDto> ExpectAsync(HttpClient host, LocationDto site, string email, string phone)
    {
        var response = await PostAsync(host, "/api/v1/visits", new { siteId = site.Id, kind = "Guest", visitorName = "Layla Hassan", purpose = "Review", expectedAt = DateTimeOffset.UtcNow.AddHours(2), visitorEmail = email, visitorPhone = phone });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<CreatedVisitDto>(response)).Visit;
    }

    /// <summary>The visit as reception sees it on its day's list.</summary>
    private static async Task<VisitDto?> SeenAsync(HttpClient reception, VisitDto v) =>
        (await ReadAsync<List<VisitDto>>(await reception.GetAsync($"/api/v1/visits?day={DateOnly.FromDateTime(v.ExpectedAt.UtcDateTime):yyyy-MM-dd}"))).SingleOrDefault(x => x.Id == v.Id);

    private async Task<(string? Email, string? Phone)> StoredAsync(Guid tenant, Guid visitId)
    {
        await using var db = sql.Visitors(tenant);
        var email = await db.Database.SqlQueryRaw<string>("SELECT VisitorEmail AS Value FROM visitors.Visits WHERE Id = {0}", visitId).SingleAsync();
        var phone = await db.Database.SqlQueryRaw<string>("SELECT VisitorPhone AS Value FROM visitors.Visits WHERE Id = {0}", visitId).SingleAsync();
        return (email, phone);
    }

    [Fact]
    public async Task Contact_details_are_stored_unreadable_and_come_back_unchanged_through_the_API()
    {
        var api = Start(("k1", Key1));
        var rig = await NewRigAsync(api);
        var visit = await ExpectAsync(rig.Host, rig.Site, "layla@acme.test", "+966 55 123 4567");

        var (email, phone) = await StoredAsync(rig.Tenant, visit.Id);
        Assert.StartsWith("enc1:k1:", email);
        Assert.StartsWith("enc1:k1:", phone);
        Assert.DoesNotContain("layla", email);
        Assert.DoesNotContain("4567", phone);

        var read = await SeenAsync(rig.Reception, visit);
        Assert.Equal("layla@acme.test", read!.VisitorEmail);
        Assert.Equal("+966 55 123 4567", read.VisitorPhone);
    }

    [Fact]
    public async Task After_a_key_rotation_older_values_still_read_and_new_ones_use_the_new_key()
    {
        var first = Start(("k1", Key1));
        var rig = await NewRigAsync(first);
        var oldVisit = await ExpectAsync(rig.Host, rig.Site, "old@acme.test", "0501111111");

        var second = Start(("k1", Key1), ("k2", Key2));
        var host = await second.ClientAsync(rig.Tenant, SystemRoles.Requester);
        var reception = await second.ClientAsync(rig.Tenant, SystemRoles.Reception);
        var newVisit = await ExpectAsync(host, rig.Site, "new@acme.test", "0502222222");

        Assert.StartsWith("enc1:k1:", (await StoredAsync(rig.Tenant, oldVisit.Id)).Email);
        Assert.StartsWith("enc1:k2:", (await StoredAsync(rig.Tenant, newVisit.Id)).Email);
        Assert.Equal("old@acme.test", (await SeenAsync(reception, oldVisit))!.VisitorEmail);
        Assert.Equal("new@acme.test", (await SeenAsync(reception, newVisit))!.VisitorEmail);
    }

    [Fact]
    public async Task Without_a_key_details_are_stored_as_before_and_values_written_earlier_are_still_read_once_a_key_is_added()
    {
        var plain = new ApiHarness(sql);
        _api = plain;
        var rig = await NewRigAsync(plain);
        var visit = await ExpectAsync(rig.Host, rig.Site, "before@acme.test", "0503333333");
        Assert.Equal("before@acme.test", (await StoredAsync(rig.Tenant, visit.Id)).Email);

        var keyed = Start(("k1", Key1));
        var reception = await keyed.ClientAsync(rig.Tenant, SystemRoles.Reception);
        Assert.Equal("before@acme.test", (await SeenAsync(reception, visit))!.VisitorEmail);
    }

    [Fact]
    public async Task Another_organisation_cannot_read_them_and_the_retention_wipe_still_clears_them()
    {
        var api = Start(("k1", Key1));
        var a = await NewRigAsync(api);
        var b = await NewRigAsync(api);
        var visit = await ExpectAsync(a.Host, a.Site, "secret@acme.test", "0504444444");
        Assert.Null(await SeenAsync(b.Reception, visit));
        Assert.Equal("secret@acme.test", (await SeenAsync(a.Reception, visit))!.VisitorEmail);

        await using (var db = sql.Visitors(a.Tenant))
        {
            var row = await db.Visits.SingleAsync(v => v.Id == visit.Id);
            row.VisitorEmail = null;
            row.VisitorPhone = null;
            await db.SaveChangesAsync();
        }

        var (email, phone) = await StoredAsync(a.Tenant, visit.Id);
        Assert.Null(email);
        Assert.Null(phone);
    }
}
