using System.Net;
using Microsoft.Data.SqlClient;

namespace Fms.IntegrationTests;

/// <summary>Two API servers over one database: the limit on secret-address routes must hold across both, not per server.</summary>
[Collection(SqlCollection.Name)]
public sealed class SharedRateLimitTests(SqlServerFixture sql)
{
    private static IDictionary<string, string?> Settings(bool shared) => new Dictionary<string, string?>
    {
        ["Security:RateLimit:SecretRoutesPerMinute"] = "3",
        ["Security:RateLimit:Shared"] = shared ? "true" : "false",
    };

    private static async Task<HttpStatusCode> GuessAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/odata/WorkOrders");
        request.Headers.Add("X-Api-Key", "fmsf_" + Guid.NewGuid().ToString("N") + "xxxxxxxx");
        return (await client.SendAsync(request)).StatusCode;
    }

    private async Task ClearAsync()
    {
        await using var conn = new SqlConnection(sql.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("DELETE FROM dbo.RateLimitWindows", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task With_the_shared_counter_guesses_are_limited_across_servers()
    {
        await ClearAsync();
        using var one = new ApiHarness(sql, settings: Settings(true));
        using var two = new ApiHarness(sql, settings: Settings(true));
        var a = one.GuestClient();
        var b = two.GuestClient();

        var results = new List<HttpStatusCode>();
        foreach (var client in new[] { a, b, a, b, a, b }) results.Add(await GuessAsync(client));

        Assert.All(results.Take(3), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.All(results.Skip(3), s => Assert.Equal(HttpStatusCode.TooManyRequests, s)); // neither server alone had seen four
        var blocked = await b.GetAsync("/api/v1/odata/WorkOrders");
        Assert.True(blocked.Headers.TryGetValues("Retry-After", out var retry));
        Assert.InRange(int.Parse(retry!.Single()), 1, 60);
        Assert.Equal("rate_limited", System.Text.Json.JsonDocument.Parse(await blocked.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Without_it_each_server_keeps_its_own_count()
    {
        await ClearAsync();
        using var one = new ApiHarness(sql, settings: Settings(false));
        using var two = new ApiHarness(sql, settings: Settings(false));
        var results = new List<HttpStatusCode>();
        foreach (var client in new[] { one.GuestClient(), two.GuestClient(), one.GuestClient(), two.GuestClient() }) results.Add(await GuessAsync(client));
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, results); // two each, under the limit of three
    }

    [Fact]
    public async Task Ordinary_routes_are_not_counted_in_the_shared_table()
    {
        await ClearAsync();
        using var api = new ApiHarness(sql, settings: Settings(true));
        for (var i = 0; i < 5; i++) await api.GuestClient().GetAsync("/api/v1/system/info");
        await using var conn = new SqlConnection(sql.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.RateLimitWindows", conn);
        Assert.Equal(0, (int)(await cmd.ExecuteScalarAsync())!);
    }
}
