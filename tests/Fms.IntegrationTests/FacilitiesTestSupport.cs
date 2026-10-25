using System.Text.Json;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Identity;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Fms.Facilities.Services;

namespace Fms.IntegrationTests;

internal sealed class TestUser(Guid? userId = null) : ICurrentUser
{
    public bool IsAuthenticated => true;
    public string? Subject => "test";
    public string? Provider => "entra";
    public Guid? TenantId => null;
    public Guid? UserId { get; } = userId ?? Guid.NewGuid();
}

/// <summary>The Facilities services wired against a real, restricted-login context for one tenant.</summary>
internal sealed class FacilitiesScope : IAsyncDisposable
{
    public required FacilitiesDbContext Db { get; init; }
    public required LocationService Locations { get; init; }
    public required CustomFieldService CustomFields { get; init; }
    public required Guid Tenant { get; init; }

    public static FacilitiesScope For(SqlServerFixture sql, Guid tenant, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var db = sql.Facilities(tenant);
        var audit = new AuditPort(sql.PendingFor(db), new TestUser(), clock);
        return new FacilitiesScope
        {
            Db = db,
            Tenant = tenant,
            Locations = new LocationService(db, audit, clock),
            CustomFields = new CustomFieldService(db, audit),
        };
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    public static string Code(string prefix = "L") => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    public async Task<LocationDto> NewAsync(LocationType type, Guid? parent, string? code = null, string? name = null)
    {
        var result = await Locations.CreateAsync(new CreateLocationRequest(type, parent, code ?? Code(type.ToString()[..1]), name ?? $"{type} name", null, null, null, null, null));
        Assert.True(result.IsOk, result.Message ?? string.Join(";", result.Errors?.Select(e => $"{e.Key}:{string.Join(',', e.Value)}") ?? []));
        return result.Value!;
    }

    public async Task<(LocationDto Site, LocationDto Building, LocationDto Floor, LocationDto Space)> NewChainAsync()
    {
        var site = await NewAsync(LocationType.Site, null);
        var building = await NewAsync(LocationType.Building, site.Id);
        var floor = await NewAsync(LocationType.Floor, building.Id);
        var space = await NewAsync(LocationType.Space, floor.Id);
        return (site, building, floor, space);
    }

    public static Dictionary<string, JsonElement> Fields(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;
}
