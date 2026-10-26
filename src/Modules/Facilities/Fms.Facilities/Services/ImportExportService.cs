using System.Globalization;
using System.Text;
using System.Text.Json;
using Fms.BuildingBlocks.Csv;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

public sealed record ImportRowResult(int Line, string Outcome, string? Code, IReadOnlyList<string> Errors);

/// <summary>What an import did (or, for a dry run, would do). Nothing is saved unless every row is valid.</summary>
public sealed record ImportReport(bool DryRun, bool Committed, int Total, int Created, int Failed, IReadOnlyList<ImportRowResult> Rows);

/// <summary>
/// Bulk create from CSV. Rows run through the same services and rules as the API, inside one transaction: if any row is wrong nothing is
/// saved, and the report lists every problem so the file can be fixed in one pass. A dry run does everything and then rolls back.
/// </summary>
public sealed class ImportExportService(FacilitiesDbContext db, LocationService locations, AssetService assets, IAuditPort audit)
{
    public const int MaxRows = 2000;
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxExportRows = 50_000;
    private const string CustomPrefix = "cf.";

    private static readonly string[] LocationColumns = ["type", "code", "parentCode", "name", "nameAr", "address", "timeZone", "areaSquareMetres"];
    private static readonly string[] AssetColumns =
        ["code", "name", "nameAr", "classCode", "locationCode", "parentAssetCode", "criticality", "status", "manufacturer", "model", "serialNumber", "installDate", "warrantyExpiry"];

    // ---- import ----

    public Task<Result<ImportReport>> ImportLocationsAsync(string csv, bool dryRun, CancellationToken ct = default) =>
        ImportAsync(csv, dryRun, LocationColumns, ["type", "code", "name"], CustomFieldTarget.Location, "locations", ImportLocationRowsAsync, ct);

    public Task<Result<ImportReport>> ImportAssetsAsync(string csv, bool dryRun, CancellationToken ct = default) =>
        ImportAsync(csv, dryRun, AssetColumns, ["code", "name", "classCode", "locationCode"], CustomFieldTarget.Asset, "assets", ImportAssetRowsAsync, ct);

    private delegate Task<List<ImportRowResult>> RowImporter(Table table, IReadOnlyList<CustomFieldDefinition> definitions, CancellationToken ct);

    private async Task<Result<ImportReport>> ImportAsync(
        string csv, bool dryRun, string[] known, string[] required, CustomFieldTarget target, string kind, RowImporter importRows, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(csv) > MaxBytes) return Result<ImportReport>.Invalid("file", $"The file is larger than {MaxBytes / (1024 * 1024)} MB.");

        var definitions = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.Target == target && !d.IsArchived).ToListAsync(ct);
        var table = ReadTable(csv, known, required, definitions);
        if (!table.IsOk) return table.Map(_ => (ImportReport)null!);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rows = await importRows(table.Value!, definitions, ct);
            var failed = rows.Count(r => r.Outcome == "Error");
            var commit = !dryRun && failed == 0;

            if (commit)
            {
                audit.Record($"import.{kind}", kind, null, JsonSerializer.Serialize(new { rows = rows.Count }));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                await tx.RollbackAsync(ct);
            }

            db.ChangeTracker.Clear();
            return Result<ImportReport>.Ok(new ImportReport(dryRun, commit, rows.Count, rows.Count(r => r.Outcome == "Created"), failed, rows));
        });
    }

    private async Task<List<ImportRowResult>> ImportLocationRowsAsync(Table t, IReadOnlyList<CustomFieldDefinition> defs, CancellationToken ct)
    {
        var byCode = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in await db.Locations.AsNoTracking().Where(l => !l.IsArchived).Select(l => new { l.Code, l.Id }).ToListAsync(ct)) byCode[l.Code] = l.Id;

        var results = new List<ImportRowResult>();
        foreach (var row in t.Rows)
        {
            var errors = new List<string>();
            var code = row.Get("code");

            if (!Enum.TryParse<LocationType>(row.Get("type"), true, out var type) || !Enum.IsDefined(type)) errors.Add("type: use Site, Building, Floor or Space.");
            Guid? parentId = null;
            if (row.Get("parentCode") is { Length: > 0 } parentCode)
            {
                if (byCode.TryGetValue(parentCode, out var pid)) parentId = pid;
                else errors.Add($"parentCode: no active location with code '{parentCode}' (it must exist already or appear earlier in the file).");
            }

            var area = ParseDecimal(row, "areaSquareMetres", errors);
            var fields = ParseCustomFields(row, defs, errors);
            if (errors.Count == 0)
            {
                var r = await locations.CreateAsync(new CreateLocationRequest(type, parentId, code, row.Get("name"), Blank(row.Get("nameAr")), Blank(row.Get("address")),
                    Blank(row.Get("timeZone")), area, fields));
                if (r.IsOk) byCode[r.Value!.Code] = r.Value.Id;
                else errors.AddRange(Describe(r));
            }

            results.Add(Result(row, code, errors));
        }

        return results;
    }

    private async Task<List<ImportRowResult>> ImportAssetRowsAsync(Table t, IReadOnlyList<CustomFieldDefinition> defs, CancellationToken ct)
    {
        var classes = (await db.AssetClasses.AsNoTracking().Where(c => !c.IsArchived).Select(c => new { c.Code, c.Id }).ToListAsync(ct)).ToDictionary(c => c.Code, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var places = (await db.Locations.AsNoTracking().Where(l => !l.IsArchived).Select(l => new { l.Code, l.Id }).ToListAsync(ct)).ToDictionary(l => l.Code, l => l.Id, StringComparer.OrdinalIgnoreCase);
        var assetIds = (await db.Assets.AsNoTracking().Select(a => new { a.Code, a.Id }).ToListAsync(ct)).ToDictionary(a => a.Code, a => a.Id, StringComparer.OrdinalIgnoreCase);

        var results = new List<ImportRowResult>();
        foreach (var row in t.Rows)
        {
            var errors = new List<string>();
            var code = row.Get("code");

            if (!classes.TryGetValue(row.Get("classCode"), out var classId)) errors.Add($"classCode: no active asset class with code '{row.Get("classCode")}'.");
            if (!places.TryGetValue(row.Get("locationCode"), out var locationId)) errors.Add($"locationCode: no active location with code '{row.Get("locationCode")}'.");
            Guid? parentId = null;
            if (row.Get("parentAssetCode") is { Length: > 0 } parentCode)
            {
                if (assetIds.TryGetValue(parentCode, out var pid)) parentId = pid;
                else errors.Add($"parentAssetCode: no asset with code '{parentCode}' (it must exist already or appear earlier in the file).");
            }

            Criticality? criticality = null;
            if (row.Get("criticality") is { Length: > 0 } c)
            {
                if (Enum.TryParse<Criticality>(c, true, out var parsed) && Enum.IsDefined(parsed)) criticality = parsed;
                else errors.Add("criticality: use Low, Medium, High or Critical.");
            }

            var startActive = false;
            if (row.Get("status") is { Length: > 0 } status)
            {
                if (status.Equals("Active", StringComparison.OrdinalIgnoreCase)) startActive = true;
                else if (!status.Equals("Planned", StringComparison.OrdinalIgnoreCase)) errors.Add("status: an imported asset starts as Planned or Active.");
            }

            var install = ParseDate(row, "installDate", errors);
            var warranty = ParseDate(row, "warrantyExpiry", errors);
            var fields = ParseCustomFields(row, defs, errors);
            if (errors.Count == 0)
            {
                var r = await assets.CreateAsync(new CreateAssetRequest(code, row.Get("name"), Blank(row.Get("nameAr")), classId, locationId, parentId, criticality,
                    Blank(row.Get("manufacturer")), Blank(row.Get("model")), Blank(row.Get("serialNumber")), install, warranty, fields, startActive));
                if (r.IsOk) assetIds[r.Value!.Code] = r.Value.Id;
                else errors.AddRange(Describe(r));
            }

            results.Add(Result(row, code, errors));
        }

        return results;
    }

    // ---- export ----

    public async Task<Result<string>> ExportLocationsAsync(CancellationToken ct = default)
    {
        var rows = await db.Locations.AsNoTracking().Where(l => !l.IsArchived).OrderBy(l => l.Path).Take(MaxExportRows + 1).ToListAsync(ct);
        if (rows.Count > MaxExportRows) return Result<string>.Conflict($"Too many locations to export at once (more than {MaxExportRows}).");

        var codes = rows.ToDictionary(l => l.Id, l => l.Code);
        var defs = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.Target == CustomFieldTarget.Location && !d.IsArchived).OrderBy(d => d.SortOrder).ThenBy(d => d.Key).ToListAsync(ct);
        var csv = new StringBuilder().Append('﻿').AppendLine(Csv.Line(LocationColumns.Concat(defs.Select(d => CustomPrefix + d.Key))));
        foreach (var l in rows)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l.CustomFieldsJson) ?? [];
            csv.AppendLine(Csv.Line(new[]
            {
                l.Type.ToString(), l.Code, l.ParentId is { } p && codes.TryGetValue(p, out var pc) ? pc : null, l.Name, l.NameAr, l.Address, l.TimeZone,
                l.AreaSquareMetres?.ToString(CultureInfo.InvariantCulture),
            }.Concat(defs.Select(d => FieldText(values, d.Key)))));
        }

        return Result<string>.Ok(csv.ToString());
    }

    public async Task<Result<string>> ExportAssetsAsync(CancellationToken ct = default)
    {
        var rows = await db.Assets.AsNoTracking().OrderBy(a => a.Code).Take(MaxExportRows + 1).ToListAsync(ct);
        if (rows.Count > MaxExportRows) return Result<string>.Conflict($"Too many assets to export at once (more than {MaxExportRows}).");

        var classCodes = await db.AssetClasses.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var locationCodes = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        var assetCodes = rows.ToDictionary(a => a.Id, a => a.Code);
        var defs = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.Target == CustomFieldTarget.Asset && !d.IsArchived).OrderBy(d => d.SortOrder).ThenBy(d => d.Key).ToListAsync(ct);

        var csv = new StringBuilder().Append('﻿').AppendLine(Csv.Line(AssetColumns.Concat(defs.Select(d => CustomPrefix + d.Key))));
        foreach (var a in rows)
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.CustomFieldsJson) ?? [];
            csv.AppendLine(Csv.Line(new[]
            {
                a.Code, a.Name, a.NameAr, classCodes.GetValueOrDefault(a.AssetClassId), locationCodes.GetValueOrDefault(a.LocationId),
                a.ParentAssetId is { } p ? assetCodes.GetValueOrDefault(p) : null, a.Criticality.ToString(), a.Status.ToString(), a.Manufacturer, a.Model,
                a.SerialNumber, a.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), a.WarrantyExpiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            }.Concat(defs.Select(d => FieldText(values, d.Key)))));
        }

        return Result<string>.Ok(csv.ToString());
    }

    // ---- CSV table ----

    private sealed class Table(IReadOnlyList<Row> rows) { public IReadOnlyList<Row> Rows { get; } = rows; }

    private sealed class Row(int line, IReadOnlyDictionary<string, string> cells)
    {
        public int Line { get; } = line;
        public string Get(string column) => cells.TryGetValue(column, out var v) ? v : "";
        public IEnumerable<KeyValuePair<string, string>> Custom => cells.Where(c => c.Key.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static Result<Table> ReadTable(string csv, string[] known, string[] required, IReadOnlyList<CustomFieldDefinition> defs)
    {
        IReadOnlyList<string[]> lines;
        try { lines = Csv.Parse(csv); }
        catch (FormatException ex) { return Result<Table>.Invalid("file", ex.Message); }

        if (lines.Count == 0) return Result<Table>.Invalid("file", "The file is empty. The first line must be the column names.");
        var header = lines[0].Select(h => h.Trim()).ToArray();
        var errors = new ValidationErrors();

        var duplicate = header.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) errors.Add("file", $"Column '{duplicate.Key}' appears twice.");
        foreach (var h in header)
        {
            var isCustom = h.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase) && defs.Any(d => string.Equals(CustomPrefix + d.Key, h, StringComparison.OrdinalIgnoreCase));
            if (!known.Contains(h, StringComparer.OrdinalIgnoreCase) && !isCustom) errors.Add("file", $"Unknown column '{h}'.");
        }

        foreach (var r in required.Where(r => !header.Contains(r, StringComparer.OrdinalIgnoreCase))) errors.Add("file", $"Missing required column '{r}'.");
        if (lines.Count - 1 > MaxRows) errors.Add("file", $"At most {MaxRows} rows per file.");
        if (lines.Count == 1) errors.Add("file", "The file has column names but no rows.");
        if (errors.Any) return errors.ToResult<Table>();

        var rows = new List<Row>();
        for (var i = 1; i < lines.Count; i++)
        {
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < header.Length; c++) cells[header[c]] = c < lines[i].Length ? lines[i][c].Trim() : "";
            rows.Add(new Row(i + 1, cells)); // +1: line numbers are 1-based and the header is line 1
        }

        return Result<Table>.Ok(new Table(rows));
    }

    // ---- cell helpers ----

    private static string? Blank(string v) => v.Length == 0 ? null : v;

    private static decimal? ParseDecimal(Row row, string column, List<string> errors)
    {
        var v = row.Get(column);
        if (v.Length == 0) return null;
        if (decimal.TryParse(Csv.NormalizeDigits(v), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        errors.Add($"{column}: '{v}' is not a number.");
        return null;
    }

    private static DateOnly? ParseDate(Row row, string column, List<string> errors)
    {
        var v = row.Get(column);
        if (v.Length == 0) return null;
        if (DateOnly.TryParseExact(Csv.NormalizeDigits(v), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        errors.Add($"{column}: '{v}' is not a date. Use yyyy-MM-dd, for example 2026-10-25.");
        return null;
    }

    private static Dictionary<string, JsonElement>? ParseCustomFields(Row row, IReadOnlyList<CustomFieldDefinition> defs, List<string> errors)
    {
        Dictionary<string, JsonElement>? values = null;
        foreach (var (column, raw) in row.Custom)
        {
            var text = raw.Trim();
            if (text.Length == 0) continue;
            var def = defs.First(d => string.Equals(CustomPrefix + d.Key, column, StringComparison.OrdinalIgnoreCase));
            JsonElement? parsed = def.DataType switch
            {
                CustomFieldType.Number when decimal.TryParse(Csv.NormalizeDigits(text), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) => JsonSerializer.SerializeToElement(n),
                CustomFieldType.Boolean when ParseBool(text) is { } b => JsonSerializer.SerializeToElement(b),
                CustomFieldType.Text or CustomFieldType.Choice or CustomFieldType.Date => JsonSerializer.SerializeToElement(def.DataType == CustomFieldType.Date ? Csv.NormalizeDigits(text) : text),
                _ => null,
            };

            if (parsed is null) errors.Add($"{column}: '{text}' is not a valid {def.DataType.ToString().ToLowerInvariant()} value.");
            else (values ??= [])[def.Key] = parsed.Value;
        }

        return values;
    }

    private static bool? ParseBool(string text) => text.ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "1" or "نعم" => true,
        "false" or "no" or "n" or "0" or "لا" => false,
        _ => null,
    };

    private static string? FieldText(Dictionary<string, JsonElement> values, string key) =>
        !values.TryGetValue(key, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => v.GetDecimal().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

    private static IEnumerable<string> Describe<T>(Result<T> r)
    {
        if (r.Errors is not null)
        {
            foreach (var (field, messages) in r.Errors)
                foreach (var m in messages) yield return $"{field}: {m}";
        }
        else
        {
            yield return r.Message ?? r.Status.ToString();
        }
    }

    private static ImportRowResult Result(Row row, string code, List<string> errors) =>
        new(row.Line, errors.Count == 0 ? "Created" : "Error", code.Length == 0 ? null : code, errors);
}
