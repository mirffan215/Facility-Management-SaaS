using Fms.BuildingBlocks.Security;
using Fms.Energy.Domain;
using Fms.Energy.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Fms.Api;

public static class EnergyEndpoints
{
    public static void MapEnergy(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/energy");

        g.MapGet("/meters", async (MeterService s, EnergyKind? kind, Guid? locationId, string? q, bool? includeArchived, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(kind, locationId, q, includeArchived ?? false, page, pageSize, ct))).RequirePermission(Permissions.EnergyRead);
        g.MapGet("/meters/{id:guid}", async (Guid id, MeterService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.EnergyRead);
        g.MapPost("/meters", async (SaveEnergyMeterRequest r, MeterService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/energy/meters/{x.Id}", x))).RequirePermission(Permissions.EnergyManage);
        g.MapPut("/meters/{id:guid}", async (Guid id, SaveEnergyMeterRequest r, MeterService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.EnergyManage);
        g.MapPost("/meters/{id:guid}/archive", async (Guid id, ArchiveEnergyMeterRequest r, MeterService s, CancellationToken ct) => (await s.ArchiveAsync(id, true, r, ct)).ToHttp()).RequirePermission(Permissions.EnergyManage);
        g.MapPost("/meters/{id:guid}/restore", async (Guid id, ArchiveEnergyMeterRequest r, MeterService s, CancellationToken ct) => (await s.ArchiveAsync(id, false, r, ct)).ToHttp()).RequirePermission(Permissions.EnergyManage);

        g.MapGet("/meters/{id:guid}/readings", async (Guid id, ReadingService s, DateOnly? from, DateOnly? to, int? page, int? pageSize, CancellationToken ct) => (await s.ListAsync(id, from, to, page, pageSize, ct)).ToHttp()).RequirePermission(Permissions.EnergyRead);
        g.MapPost("/meters/{id:guid}/readings", async (Guid id, AddReadingRequest r, ReadingService s, CancellationToken ct) => (await s.AddAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/energy/meters/{id}/readings", x))).RequirePermission(Permissions.EnergyRecord);
        g.MapPost("/readings/{id:guid}/void", async (Guid id, VoidReadingRequest r, ReadingService s, CancellationToken ct) => (await s.VoidAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.EnergyRecord);

        // multipart/form-data: file (a CSV with the columns meter, at, value[, note]) and dryRun (true to check without saving).
        g.MapPost("/readings/import", async (HttpContext http, ReadingService s, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Send the file as multipart/form-data.");
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = ReadingService.MaxImportBytes + 1024 * 1024;
                IFormCollection form;
                try { form = await http.Request.ReadFormAsync(ct); }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large."); }
                catch (InvalidDataException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload could not be read."); }
                if (form.Files.Count != 1 || form.Files[0].Name != "file")
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send exactly one file in the 'file' field."] });
                var dryRun = !form.TryGetValue("dryRun", out var d) || !string.Equals(d.ToString(), "false", StringComparison.OrdinalIgnoreCase);
                await using var stream = form.Files[0].OpenReadStream();
                return (await s.ImportAsync(stream, dryRun, ct)).ToHttp();
            })
            .RequirePermission(Permissions.EnergyRecord);

        g.MapGet("/consumption", async (ConsumptionService s, DateOnly? from, DateOnly? to, EnergyKind? kind, Guid? locationId, Guid? meterId, CancellationToken ct) =>
            (await s.ComputeAsync(from, to, kind, locationId, meterId, ct)).ToHttp()).RequirePermission(Permissions.EnergyRead);

        g.MapGet("/tariffs", async (TariffService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequirePermission(Permissions.EnergyRead);
        g.MapPost("/tariffs", async (SaveTariffRequest r, TariffService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/energy/tariffs", x))).RequirePermission(Permissions.EnergyManage);
        g.MapPut("/tariffs/{id:guid}", async (Guid id, SaveTariffRequest r, TariffService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.EnergyManage);

        g.MapGet("/devices", async (DeviceService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequirePermission(Permissions.EnergyManage);
        g.MapPost("/devices", async (CreateDeviceRequest r, DeviceService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/energy/devices", x))).RequirePermission(Permissions.EnergyManage);
        g.MapPost("/devices/{id:guid}/revoke", async (Guid id, DeviceService s, CancellationToken ct) => (await s.RevokeAsync(id, ct)).ToHttp()).RequirePermission(Permissions.EnergyManage);

        g.MapGet("/alarms/rules", async (AlarmService s, CancellationToken ct) => Results.Ok(await s.RulesAsync(ct))).RequirePermission(Permissions.EnergyRead);
        g.MapPost("/alarms/rules", async (SaveAlarmRuleRequest r, AlarmService s, CancellationToken ct) => (await s.SaveAsync(null, r, ct)).ToHttp(x => Results.Created($"/api/v1/energy/alarms/rules", x))).RequirePermission(Permissions.EnergyManage);
        g.MapPut("/alarms/rules/{id:guid}", async (Guid id, SaveAlarmRuleRequest r, AlarmService s, CancellationToken ct) => (await s.SaveAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.EnergyManage);
        g.MapPost("/alarms/events/{id:guid}/work-order", async (Guid id, AlarmService s, CancellationToken ct) => (await s.RaiseWorkOrderAsync(id, ct)).ToHttp()).RequirePermission(Permissions.EnergyManage);
        g.MapGet("/alarms/events", async (AlarmService s, Guid? meterId, int? page, int? pageSize, CancellationToken ct) => Results.Ok(await s.EventsAsync(meterId, page, pageSize, ct))).RequirePermission(Permissions.EnergyRead);

        // A device posts here. It has no sign-in: its secret (Authorization: Bearer fmse_... or X-Api-Key) names the organisation and the meters it may report for.
        // Every missing, wrong, expired or revoked secret gets the same answer.
        g.MapPost("/ingest", async (HttpContext http, IngestRequest r, DeviceService devices, ReadingService readings, CancellationToken ct) =>
            {
                var access = await devices.AuthenticateAsync(http.Request.Headers.Authorization, http.Request.Headers["X-Api-Key"], ct);
                if (access is null) return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
                return (await readings.IngestAsync(access, r, ct)).ToHttp();
            })
            .AllowAnonymous();
    }
}
