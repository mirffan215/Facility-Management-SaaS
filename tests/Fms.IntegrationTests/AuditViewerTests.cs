using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class AuditViewerTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Admin, HttpClient Manager, Guid AdminId);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Aud " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var admin = await _api.ClientAsync(tenant, SystemRoles.Administrator);
        return new Rig(tenant, admin, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), (await admin.GetFromJsonAsync<Fms.Api.MeInfo>("/api/v1/me"))!.UserId!.Value);
    }

    private static async Task<Page<AuditEntryDto>> SearchAsync(HttpClient c, string query = "")
    {
        var response = await c.GetAsync("/api/v1/audit" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<Page<AuditEntryDto>>(response);
    }

    [Fact]
    public async Task Only_people_with_the_audit_right_can_search_or_export()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Manager.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Manager.GetAsync("/api/v1/audit/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Manager.GetAsync("/api/v1/audit/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Admin.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task The_trail_is_searched_by_person_action_thing_and_period_and_names_who_acted()
    {
        var rig = await NewRigAsync();
        var vendor = await PostAsync(rig.Admin, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "CoolAir" });
        Assert.Equal(HttpStatusCode.Created, vendor.StatusCode);
        await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" });

        var all = await SearchAsync(rig.Admin);
        Assert.Contains(all.Items, e => e.Action == "vendor.created");
        Assert.True(all.Items.Select(e => e.OccurredAt).SequenceEqual(all.Items.Select(e => e.OccurredAt).OrderByDescending(x => x))); // newest first

        var mine = await SearchAsync(rig.Admin, $"?actorId={rig.AdminId}");
        Assert.All(mine.Items, e => Assert.Equal(rig.AdminId, e.ActorId));
        Assert.Contains(mine.Items, e => e.Action == "vendor.created");
        Assert.DoesNotContain(mine.Items, e => e.Action.StartsWith("location."));

        var byAction = await SearchAsync(rig.Admin, "?action=vendor");
        Assert.NotEmpty(byAction.Items);
        Assert.All(byAction.Items, e => Assert.StartsWith("vendor", e.Action));
        var exact = await SearchAsync(rig.Admin, "?action=vendor.created");
        Assert.All(exact.Items, e => Assert.Equal("vendor.created", e.Action));
        Assert.Empty((await SearchAsync(rig.Admin, "?action=ven")).Items); // a prefix means whole words: "vendor", not "ven"

        var one = exact.Items.First();
        var byThing = await SearchAsync(rig.Admin, $"?entityType={one.EntityType}&entityId={one.EntityId}");
        Assert.Contains(byThing.Items, e => e.Id == one.Id);
        Assert.Contains("CoolAir", one.Summary ?? "");

        var actions = await ReadAsync<List<string>>(await rig.Admin.GetAsync("/api/v1/audit/actions"));
        Assert.Contains("vendor.created", actions);

        Assert.Empty((await SearchAsync(rig.Admin, "?from=2020-01-01&to=2020-01-31")).Items); // a past period has none
        var paged = await SearchAsync(rig.Admin, "?pageSize=1");
        Assert.Single(paged.Items);
        Assert.True(paged.Total > 1);
    }

    [Fact]
    public async Task The_period_is_validated()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Admin.GetAsync("/api/v1/audit?from=2027-03-10&to=2027-03-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Admin.GetAsync("/api/v1/audit?from=2020-01-01&to=2027-03-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Admin.GetAsync("/api/v1/audit/export.csv?from=2027-03-10&to=2027-03-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Admin.GetAsync("/api/v1/audit?from=nope")).StatusCode);
    }

    [Fact]
    public async Task An_export_has_what_was_searched_and_is_itself_recorded()
    {
        var rig = await NewRigAsync();
        await PostAsync(rig.Admin, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "Exported Vendor" });

        var response = await rig.Admin.GetAsync("/api/v1/audit/export.csv?action=vendor");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("audit-", response.Content.Headers.ContentDisposition!.FileName);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("﻿OccurredAt,Actor,ActorId,Action,EntityType,EntityId,Summary,CorrelationId", csv);
        Assert.Contains("vendor.created", csv);
        Assert.Equal("false", response.Headers.GetValues("X-Export-Truncated").Single());
        Assert.DoesNotContain("location.", csv); // only what the filter matched

        var recorded = await SearchAsync(rig.Admin, "?action=audit.exported");
        var entry = Assert.Single(recorded.Items);
        Assert.Equal(rig.AdminId, entry.ActorId);
        Assert.Contains("\"rows\"", entry.Summary);
    }

    [Fact]
    public async Task Another_organisation_sees_only_its_own_trail()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        await PostAsync(a.Admin, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "Only A" });
        Assert.DoesNotContain("Only A", string.Join(' ', (await SearchAsync(b.Admin)).Items.Select(e => e.Summary)));
        var csv = await (await b.Admin.GetAsync("/api/v1/audit/export.csv")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Only A", csv);
        Assert.Empty((await SearchAsync(b.Admin, "?action=vendor")).Items);
    }
}
