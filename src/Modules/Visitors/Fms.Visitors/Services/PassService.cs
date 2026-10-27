using Fms.BuildingBlocks.Tenancy;
using Fms.Visitors.Domain;
using Fms.Visitors.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using QRCoder;

namespace Fms.Visitors.Services;

/// <summary>The visitor's own pass: the address they open and the QR code reception scans. Neither needs the visitor to have an account.</summary>
public sealed class PassService(VisitorsDbContext db, TenantContext tenant, IConfiguration configuration)
{
    /// <summary>The address a pass lives at, on the web app's public address.</summary>
    public string UrlFor(string code)
    {
        var baseUrl = (configuration["Visitors:PublicBaseUrl"] ?? configuration["Facilities:PublicBaseUrl"])?.TrimEnd('/');
        return string.IsNullOrEmpty(baseUrl) ? $"/visit/{code}" : $"{baseUrl}/visit/{code}";
    }

    public string QrSvg(string code)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(UrlFor(code), QRCodeGenerator.ECCLevel.Q);
        return new SvgQRCode(data).GetGraphic(new System.Drawing.Size(256, 256), drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>
    /// The pass for a code, or null when it is not valid (always the same answer, so codes cannot be probed). The caller has no tenant (a visitor is anonymous), so this
    /// looks the code up across organisations and returns only what the visitor needs to see.
    /// </summary>
    public async Task<PassDto?> ViewAsync(string? code, CancellationToken ct = default)
    {
        var clean = VisitorRules.CodeFrom(code);
        if (clean is null) return null;
        using var platform = tenant.BeginPlatformScope();
        var hash = Common.Hash(clean);
        var v = await db.Visits.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.PassHash == hash && x.AnonymisedAt == null, ct);
        return v is null ? null : new PassDto(v.VisitorName, v.SiteName, v.HostName, v.ExpectedAt, v.ExpectedUntil, v.Status);
    }

    public async Task<string?> QrFor(string? code, CancellationToken ct = default) => await ViewAsync(code, ct) is null ? null : QrSvg(VisitorRules.CodeFrom(code)!);
}
