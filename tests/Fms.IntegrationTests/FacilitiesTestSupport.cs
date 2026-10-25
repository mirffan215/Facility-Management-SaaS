using System.Text.Json;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Identity;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Fms.BuildingBlocks.Storage;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities.Services;
using Microsoft.Extensions.Options;

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
    public required AssetClassService Classes { get; init; }
    public required AssetService Assets { get; init; }
    public required MeterService Meters { get; init; }
    public required DocumentService Documents { get; init; }
    public Guid UserId { get; init; }
    public required Guid Tenant { get; init; }

    public static FacilitiesScope For(SqlServerFixture sql, Guid tenant, TimeProvider? clock = null, IFileScanner? scanner = null, FacilitiesOptions? options = null)
    {
        clock ??= TimeProvider.System;
        var db = sql.Facilities(tenant);
        var user = new TestUser();
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenant);
        var audit = new AuditPort(sql.PendingFor(db), user, clock);
        return new FacilitiesScope
        {
            Db = db,
            Tenant = tenant,
            Locations = new LocationService(db, audit, clock),
            CustomFields = new CustomFieldService(db, audit),
            Classes = new AssetClassService(db, audit),
            Assets = new AssetService(db, audit, clock),
            Meters = new MeterService(db, audit, user, clock),
            Documents = new DocumentService(db, new TenantScopedFileStore(sql.Blobs, tenantContext), scanner ?? new NoOpFileScanner(), audit, user, Options.Create(options ?? new FacilitiesOptions()), clock),
            UserId = user.UserId!.Value,
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

    public async Task<AssetClassDto> NewClassAsync(string? code = null, Guid? parent = null)
    {
        var r = await Classes.CreateAsync(new SaveAssetClassRequest(parent, code ?? Code("C"), "Class " + Guid.NewGuid().ToString("N")[..6], null, null));
        Assert.True(r.IsOk, r.Message ?? string.Join(";", r.Errors?.Select(e => $"{e.Key}:{string.Join(',', e.Value)}") ?? []));
        return r.Value!;
    }

    public static CreateAssetRequest AssetRequest(Guid classId, Guid locationId, string? code = null, Guid? parent = null, bool active = false,
        DateOnly? install = null, DateOnly? warranty = null, string? name = null, string? serial = null, Dictionary<string, JsonElement>? fields = null) =>
        new(code ?? Code("A"), name ?? "Asset", null, classId, locationId, parent, null, null, null, serial, install, warranty, fields, active);

    public async Task<AssetDto> NewAssetAsync(Guid classId, Guid locationId, string? code = null, Guid? parent = null, bool active = false, string? name = null)
    {
        var r = await Assets.CreateAsync(AssetRequest(classId, locationId, code, parent, active, name: name));
        Assert.True(r.IsOk, r.Message ?? string.Join(";", r.Errors?.Select(e => $"{e.Key}:{string.Join(',', e.Value)}") ?? []));
        return r.Value!;
    }

    public static SaveAssetRequest ToSave(AssetDto a) =>
        new(a.Code, a.Name, a.NameAr, a.AssetClassId, a.LocationId, a.ParentAssetId, a.Criticality, a.Manufacturer, a.Model, a.SerialNumber,
            a.InstallDate, a.WarrantyExpiry, a.CustomFields.ToDictionary(k => k.Key, k => k.Value), a.RowVersion);

    public async Task<MeterDto> NewMeterAsync(Guid assetId, MeterKind kind = MeterKind.Cumulative, string? name = null, decimal? min = null, decimal? max = null)
    {
        var r = await Meters.CreateAsync(assetId, new CreateMeterRequest(name ?? "Meter " + Guid.NewGuid().ToString("N")[..6], null, kind == MeterKind.Cumulative ? "kWh" : "°C", kind, min, max));
        Assert.True(r.IsOk, r.Message ?? string.Join(";", r.Errors?.Select(e => $"{e.Key}:{string.Join(',', e.Value)}") ?? []));
        return r.Value!;
    }

    public static Dictionary<string, JsonElement> Fields(object values) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;
}
