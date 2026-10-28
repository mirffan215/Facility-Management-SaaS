using System.Security.Cryptography;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Energy.Domain;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Services;

/// <summary>
/// Data loggers and gateways that post readings. A device is limited to the meters it was given, its secret is shown once (only a hash is kept), it expires, and it can be
/// revoked at once. The device has no sign-in, so its secret is looked up across organisations first and the rest of the request then belongs to that one.
/// </summary>
public sealed class DeviceService(EnergyDbContext db, TenantContext tenant, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxActive = 100;
    public const int MaxMetersPerDevice = 200;
    public const int MaxDays = 1095;
    public const string SecretPrefix = "fmse_";

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return (await db.Devices.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(300).ToListAsync(ct)).Select(d => ToDto(d, now)).ToList();
    }

    public async Task<Result<CreatedDeviceDto>> CreateAsync(CreateDeviceRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 100) errors.Add("name", "Name the device (at most 100 characters).");
        var days = r.ExpiresInDays ?? 365;
        if (days is < 1 or > MaxDays) errors.Add("expiresInDays", $"Access lasts from 1 to {MaxDays} days.");
        var ids = (r.MeterIds ?? []).Distinct().ToList();
        if (ids.Count == 0) errors.Add("meterIds", "Choose at least one meter the device may send readings for.");
        else if (ids.Count > MaxMetersPerDevice) errors.Add("meterIds", $"At most {MaxMetersPerDevice} meters per device.");
        else if (await db.Meters.CountAsync(m => ids.Contains(m.Id) && !m.IsArchived, ct) != ids.Count) errors.Add("meterIds", "One of the chosen meters does not exist or is archived.");
        if (errors.Any) return errors.ToResult<CreatedDeviceDto>();

        var now = clock.GetUtcNow();
        if (await db.Devices.CountAsync(d => d.RevokedAt == null && d.ExpiresAt > now, ct) >= MaxActive) return Result<CreatedDeviceDto>.Conflict($"At most {MaxActive} devices can be active. Revoke one first.");

        var secret = SecretPrefix + Common.Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new Device
        {
            Name = name!, Prefix = secret[..10], TokenHash = Common.Hash(secret), MeterIdsJson = JsonSerializer.Serialize(ids), CreatedBy = user.UserId, CreatedAt = now, ExpiresAt = now.AddDays(days),
        };
        db.Devices.Add(device);
        audit.Record("energy.device_created", nameof(Device), device.Id.ToString(), JsonSerializer.Serialize(new { device.Name, meters = ids.Count, device.ExpiresAt }));
        await db.SaveChangesAsync(ct);
        return Result<CreatedDeviceDto>.Ok(new CreatedDeviceDto(ToDto(device, now), secret, "/api/v1/energy/ingest"));
    }

    public async Task<Result<DeviceDto>> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var d = await db.Devices.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Result<DeviceDto>.NotFound();
        if (d.RevokedAt is null)
        {
            d.RevokedAt = clock.GetUtcNow();
            d.RevokedBy = user.UserId;
            audit.Record("energy.device_revoked", nameof(Device), id.ToString(), JsonSerializer.Serialize(new { d.Name }));
            await db.SaveChangesAsync(ct);
        }

        return Result<DeviceDto>.Ok(ToDto(d, clock.GetUtcNow()));
    }

    /// <summary>The device a request is from, or null for anything invalid (always the same answer). On success the rest of the request is in that device's organisation.</summary>
    public async Task<DeviceAccess?> AuthenticateAsync(string? authorization, string? apiKey, CancellationToken ct = default)
    {
        var secret = SecretFrom(authorization, apiKey);
        if (secret is null || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length is < 20 or > 100) return null;
        var hash = Common.Hash(secret);
        var now = clock.GetUtcNow();

        Guid tenantId, deviceId;
        string meters;
        using (tenant.BeginPlatformScope())
        {
            var d = await db.Devices.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
            if (d is null || d.RevokedAt is not null || d.ExpiresAt <= now) return null;
            if (d.LastUsedAt is null || now - d.LastUsedAt > TimeSpan.FromMinutes(1))
            {
                d.LastUsedAt = now;
                await db.SaveChangesAsync(ct);
            }

            (tenantId, deviceId, meters) = (d.TenantId, d.Id, d.MeterIdsJson);
        }

        tenant.SetTenant(tenantId);
        return new DeviceAccess(tenantId, deviceId, JsonSerializer.Deserialize<List<Guid>>(meters) ?? []);
    }

    public static string? SecretFrom(string? authorization, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey)) return apiKey.Trim();
        if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return authorization["Bearer ".Length..].Trim();
        return null;
    }

    private static DeviceDto ToDto(Device d, DateTimeOffset now) =>
        new(d.Id, d.Name, d.Prefix, JsonSerializer.Deserialize<List<Guid>>(d.MeterIdsJson) ?? [], d.CreatedAt, d.ExpiresAt, d.LastUsedAt, d.RevokedAt, d.RevokedAt is not null ? "Revoked" : d.ExpiresAt <= now ? "Expired" : "Active");
}
