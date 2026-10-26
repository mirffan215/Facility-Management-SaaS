using System.Text.Json;
using System.Text.RegularExpressions;
using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Integration.Domain;
using Fms.Integration.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fms.Integration.Services;

/// <summary>Whether the tenant sends its data to an accounting system, which adapter and which company. Reading needs integration.read; changing needs integration.manage.</summary>
public sealed partial class ErpSettingsService(IntegrationDbContext db, IAuditPort audit, IEnumerable<IErpAdapter> adapters, IOptions<ErpOptions> options, TimeProvider clock)
{
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,50}$")]
    private static partial Regex CompanyPattern();

    public IReadOnlyList<string> AvailableAdapters() =>
        adapters.Select(a => a.Name).Where(n => n != FakeErpAdapter.AdapterName || options.Value.AllowFakeAdapter).OrderBy(n => n).ToList();

    public async Task<ErpSettings?> CurrentAsync(CancellationToken ct = default) => await db.ErpSettings.AsNoTracking().FirstOrDefaultAsync(ct);

    public async Task<ErpSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var s = await CurrentAsync(ct);
        return new ErpSettingsDto(s?.Enabled ?? false, s?.Adapter ?? "", s?.CompanyId ?? "", AvailableAdapters(), s is null ? null : Convert.ToBase64String(s.RowVersion));
    }

    public async Task<Result<ErpSettingsDto>> SaveAsync(SaveErpSettingsRequest r, CancellationToken ct = default)
    {
        var existing = await db.ErpSettings.FirstOrDefaultAsync(ct);
        var errors = new ValidationErrors();
        var enabled = r.Enabled ?? false;
        var adapter = r.Adapter?.Trim() ?? "";
        var company = r.CompanyId?.Trim() ?? "";
        if (enabled && !AvailableAdapters().Contains(adapter)) errors.Add("adapter", "Choose an adapter that is installed.");
        if (enabled && !CompanyPattern().IsMatch(company)) errors.Add("companyId", "Enter the company code in the accounting system (letters, digits, dash, underscore; up to 50).");
        if (!enabled && company.Length > 50) errors.Add("companyId", "The company code is at most 50 characters.");
        var version = Array.Empty<byte>();
        if (existing is not null && !TryVersion(r.RowVersion, out version)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<ErpSettingsDto>();

        var s = existing ?? new ErpSettings();
        if (enabled && !s.Enabled) s.EnabledAt = clock.GetUtcNow();
        s.Enabled = enabled;
        s.Adapter = adapter;
        s.CompanyId = company;
        if (existing is null) db.ErpSettings.Add(s);
        else
        {
            db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(s).Property(x => x.Adapter).IsModified = true;
        }

        audit.Record("erp.settings_saved", nameof(ErpSettings), s.Id.ToString(), JsonSerializer.Serialize(new { s.Enabled, s.Adapter, s.CompanyId }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ErpSettingsDto>.Conflict("The settings were changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<ErpSettingsDto>.Conflict("The settings were saved by someone else a moment ago. Reload and try again.");
        }

        return Result<ErpSettingsDto>.Ok(await GetAsync(ct));
    }

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }
}
