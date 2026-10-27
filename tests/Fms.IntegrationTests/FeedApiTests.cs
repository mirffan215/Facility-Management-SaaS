using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Reporting.Services;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class FeedApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Admin, HttpClient Manager, HttpClient Technician, LocationDto Site);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Feed " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var admin = await _api.ClientAsync(tenant, SystemRoles.Administrator);
        var site = await ReadAsync<LocationDto>(await PostAsync(admin, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, admin, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Technician), site);
    }

    private HttpClient Feed(string? secret, string how = "basic")
    {
        var client = _api.Factory.CreateClient();
        if (secret is null) return client;
        if (how == "basic") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("powerbi:" + secret)));
        else client.DefaultRequestHeaders.Add("X-Api-Key", secret);
        return client;
    }

    private static async Task<CreatedFeedTokenDto> TokenAsync(Rig rig, string name = "Power BI", object? more = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (more is not null) foreach (var p in more.GetType().GetProperties()) body[p.Name] = p.GetValue(more);
        var response = await PostAsync(rig.Admin, "/api/v1/reports/feed/tokens", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CreatedFeedTokenDto>(response);
    }

    private static async Task OrdersAsync(Rig rig, int count)
    {
        for (var i = 0; i < count; i++) Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Admin, "/api/v1/work-orders", new { title = $"Secret title {i}", type = "Emergency", locationId = rig.Site.Id })).StatusCode);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // ---------------- tokens ----------------

    [Fact]
    public async Task Only_administrators_make_tokens_the_secret_is_shown_once_and_only_its_hash_is_kept()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Manager, "/api/v1/reports/feed/tokens", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Manager.GetAsync("/api/v1/reports/feed/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync("/api/v1/reports/feed/datasets")).StatusCode);

        foreach (var bad in new object[] { new { name = "" }, new { name = new string('x', 101) }, new { name = "x", expiresInDays = 0 }, new { name = "x", expiresInDays = 5000 }, new { name = "x", datasets = new[] { "Nope" } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Admin, "/api/v1/reports/feed/tokens", bad)).StatusCode);

        var created = await TokenAsync(rig);
        Assert.StartsWith("fmsf_", created.Secret);
        Assert.Equal(created.Secret[..10], created.Token.Prefix);
        Assert.Equal(("Active", "*"), (created.Token.State, created.Token.Scope));

        var listText = await (await rig.Admin.GetAsync("/api/v1/reports/feed/tokens")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.Secret, listText);
        Assert.DoesNotContain("tokenHash", listText, StringComparison.OrdinalIgnoreCase);

        await using var db = sql.Reporting(rig.Tenant);
        var stored = await db.FeedTokens.SingleAsync();
        Assert.NotEqual(created.Secret, stored.TokenHash);
        Assert.DoesNotContain(created.Secret, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
    }

    [Fact]
    public async Task A_revoked_or_expired_token_stops_working_and_at_most_twenty_are_active()
    {
        var rig = await NewRigAsync();
        var token = await TokenAsync(rig);
        Assert.Equal(HttpStatusCode.OK, (await Feed(token.Secret).GetAsync("/api/v1/odata")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Admin.PostAsync($"/api/v1/reports/feed/tokens/{Guid.NewGuid()}/revoke", null)).StatusCode);
        var revoked = await ReadAsync<FeedTokenDto>(await rig.Admin.PostAsync($"/api/v1/reports/feed/tokens/{token.Token.Id}/revoke", null));
        Assert.Equal("Revoked", revoked.State);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Feed(token.Secret).GetAsync("/api/v1/odata")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Admin.PostAsync($"/api/v1/reports/feed/tokens/{token.Token.Id}/revoke", null)).StatusCode); // revoking twice is harmless

        var expiring = await TokenAsync(rig, "Short");
        await using (var db = sql.Reporting(rig.Tenant))
        {
            (await db.FeedTokens.SingleAsync(t => t.Id == expiring.Token.Id)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Feed(expiring.Secret).GetAsync("/api/v1/odata")).StatusCode);

        for (var i = 0; i < 20; i++) await TokenAsync(rig, "t" + i);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Admin, "/api/v1/reports/feed/tokens", new { name = "one too many" })).StatusCode);
    }

    // ---------------- the feed ----------------

    [Fact]
    public async Task The_feed_needs_a_valid_secret_and_every_failure_looks_the_same()
    {
        var rig = await NewRigAsync();
        var token = await TokenAsync(rig);
        var none = await Feed(null).GetAsync("/api/v1/odata");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("Basic", none.Headers.WwwAuthenticate.ToString()); // so Power BI asks for credentials
        foreach (var wrong in new[] { "fmsf_" + new string('x', 43), "nonsense", "fmsf_" })
        {
            var r = await Feed(wrong).GetAsync("/api/v1/odata/WorkOrders");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await Feed(token.Secret).GetAsync("/api/v1/odata")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Feed(token.Secret, "key").GetAsync("/api/v1/odata")).StatusCode);

        // A sign-in token (a person's own) is not a feed secret.
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Admin.GetAsync("/api/v1/odata/WorkOrders")).StatusCode);
        var response = await Feed(token.Secret).GetAsync("/api/v1/odata");
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("4.0", response.Headers.GetValues("OData-Version").Single());
    }

    [Fact]
    public async Task The_service_document_and_metadata_describe_the_datasets()
    {
        var rig = await NewRigAsync();
        var token = await TokenAsync(rig);
        var service = await JsonOf(await Feed(token.Secret).GetAsync("/api/v1/odata"));
        var names = service.GetProperty("value").EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToList();
        Assert.Contains("WorkOrders", names);
        Assert.Contains("StockMovements", names);
        Assert.Contains("Visits", names);

        var xml = await (await Feed(token.Secret).GetAsync("/api/v1/odata/$metadata")).Content.ReadAsStringAsync();
        XNamespace edm = "http://docs.oasis-open.org/odata/ns/edm";
        var doc = XDocument.Parse(xml);
        Assert.Contains("WorkOrders", doc.Descendants(edm + "EntitySet").Select(e => (string)e.Attribute("Name")!));

        // Nothing offered carries names, contact details or the free text people type.
        var forbidden = new[] { "VisitorName", "VisitorEmail", "VisitorPhone", "VisitorCompany", "Title", "Description", "Notes", "Email", "Phone", "Purpose", "TeamName", "Reason" };
        var columns = doc.Descendants(edm + "Property").Select(p => (string)p.Attribute("Name")!).ToList();
        Assert.Empty(columns.Intersect(forbidden));
    }

    [Fact]
    public async Task A_dataset_is_read_with_paging_count_selection_and_a_date_filter()
    {
        var rig = await NewRigAsync();
        await OrdersAsync(rig, 5);
        var token = await TokenAsync(rig);
        var feed = Feed(token.Secret);

        var all = await JsonOf(await feed.GetAsync("/api/v1/odata/WorkOrders?$count=true"));
        Assert.Equal(5, all.GetProperty("@odata.count").GetInt32());
        Assert.Equal(5, all.GetProperty("value").GetArrayLength());
        Assert.False(all.TryGetProperty("@odata.nextLink", out _));
        var row = all.GetProperty("value")[0];
        Assert.Equal("Emergency", row.GetProperty("Type").GetString());
        Assert.False(row.TryGetProperty("Title", out _)); // what people typed is not offered
        Assert.EndsWith("Z", row.GetProperty("RequestedAt").GetString());

        // Paging follows nextLink until the end, and sees each row once.
        var ids = new List<string>();
        var url = "/api/v1/odata/WorkOrders?$top=2";
        for (var pages = 0; pages < 5; pages++)
        {
            var page = await JsonOf(await feed.GetAsync(url));
            ids.AddRange(page.GetProperty("value").EnumerateArray().Select(v => v.GetProperty("Id").GetString()!));
            if (!page.TryGetProperty("@odata.nextLink", out var next)) break;
            url = new Uri(next.GetString()!).PathAndQuery;
        }

        Assert.Equal(5, ids.Count);
        Assert.Equal(5, ids.Distinct().Count());

        var selected = await JsonOf(await feed.GetAsync("/api/v1/odata/WorkOrders?$select=Number,Status&$top=1"));
        Assert.Equal(["Number", "Status"], selected.GetProperty("value")[0].EnumerateObject().Select(p => p.Name).ToArray());

        var tomorrow = DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        Assert.Equal(5, (await JsonOf(await feed.GetAsync($"/api/v1/odata/WorkOrders?$filter=RequestedAt ge {yesterday} and RequestedAt lt {tomorrow}&$count=true"))).GetProperty("@odata.count").GetInt32());
        Assert.Equal(0, (await JsonOf(await feed.GetAsync($"/api/v1/odata/WorkOrders?$filter=RequestedAt ge {tomorrow}&$count=true"))).GetProperty("@odata.count").GetInt32());

        foreach (var bad in new[] { "$top=0", "$top=5000", "$skip=-1", "$select=Nope", "$filter=Status eq 'Open'", "$filter=RequestedAt ge nonsense" })
        {
            var response = await feed.GetAsync("/api/v1/odata/WorkOrders?" + bad);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{bad} gave {response.StatusCode}");
            Assert.Contains("\"error\"", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await feed.GetAsync("/api/v1/odata/Nothing")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await feed.GetAsync("/api/v1/odata/workorders")).StatusCode); // names are not case sensitive
    }

    [Fact]
    public async Task A_token_limited_to_some_datasets_reads_only_those()
    {
        var rig = await NewRigAsync();
        await OrdersAsync(rig, 1);
        var token = await TokenAsync(rig, "Costs only", new { datasets = new[] { "WorkCosts", "WorkOrders" } });
        Assert.Equal("WorkCosts,WorkOrders", token.Token.Scope);
        var feed = Feed(token.Secret);
        Assert.Equal(HttpStatusCode.OK, (await feed.GetAsync("/api/v1/odata/WorkOrders")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await feed.GetAsync("/api/v1/odata/Visits")).StatusCode);
        var names = (await JsonOf(await feed.GetAsync("/api/v1/odata"))).GetProperty("value").EnumerateArray().Select(v => v.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["WorkCosts", "WorkOrders"], names);
    }

    [Fact]
    public async Task Each_token_reads_only_its_own_organisation()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        await OrdersAsync(a, 2);
        await OrdersAsync(b, 3);
        var tokenA = await TokenAsync(a);
        var tokenB = await TokenAsync(b);
        Assert.Equal(2, (await JsonOf(await Feed(tokenA.Secret).GetAsync("/api/v1/odata/WorkOrders?$count=true"))).GetProperty("@odata.count").GetInt32());
        Assert.Equal(3, (await JsonOf(await Feed(tokenB.Secret).GetAsync("/api/v1/odata/WorkOrders?$count=true"))).GetProperty("@odata.count").GetInt32());

        // The management screens see only their own tokens, and cannot revoke another organisation's.
        Assert.DoesNotContain(tokenB.Token.Id, (await ReadAsync<List<FeedTokenDto>>(await a.Admin.GetAsync("/api/v1/reports/feed/tokens"))).Select(t => t.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await a.Admin.PostAsync($"/api/v1/reports/feed/tokens/{tokenB.Token.Id}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Feed(tokenB.Secret).GetAsync("/api/v1/odata")).StatusCode);

        await using var db = sql.Reporting(a.Tenant);
        Assert.Equal(0, await db.FeedTokens.IgnoreQueryFilters().CountAsync(t => t.TenantId == b.Tenant));
    }

    [Fact]
    public async Task Using_a_token_is_remembered()
    {
        var rig = await NewRigAsync();
        var token = await TokenAsync(rig);
        Assert.Null((await ReadAsync<List<FeedTokenDto>>(await rig.Admin.GetAsync("/api/v1/reports/feed/tokens"))).Single().LastUsedAt);
        await Feed(token.Secret).GetAsync("/api/v1/odata");
        Assert.NotNull((await ReadAsync<List<FeedTokenDto>>(await rig.Admin.GetAsync("/api/v1/reports/feed/tokens"))).Single().LastUsedAt);
    }
}
