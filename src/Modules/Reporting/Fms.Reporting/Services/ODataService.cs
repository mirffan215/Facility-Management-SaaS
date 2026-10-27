using System.Globalization;
using System.Text;
using System.Xml;
using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Results;

namespace Fms.Reporting.Services;

public sealed record ODataPage(string Json, bool Ok = true, int Status = 200);

/// <summary>
/// A small, read-only OData v4 surface, enough for Power BI and Excel: a service document, $metadata, and entity sets with $top, $skip, $select, $count and a date $filter on the
/// dataset's time column. Anything else in $filter is refused clearly rather than ignored, so a report never silently shows unfiltered data.
/// </summary>
public static class ODataService
{
    public const int DefaultTop = 1000;
    public const string Namespace = "Fms";

    public static string ServiceDocument(string baseUrl, IEnumerable<IDatasetProvider> datasets) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@odata.context"] = baseUrl + "/$metadata",
            ["value"] = datasets.Select(d => new { name = d.Name, kind = "EntitySet", url = d.Name }).ToList(),
        });

    public static string Metadata(IEnumerable<IDatasetProvider> datasets)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
        {
            const string edmx = "http://docs.oasis-open.org/odata/ns/edmx", edm = "http://docs.oasis-open.org/odata/ns/edm";
            w.WriteStartElement("edmx", "Edmx", edmx);
            w.WriteAttributeString("Version", "4.0");
            w.WriteStartElement("edmx", "DataServices", edmx);
            w.WriteStartElement("Schema", edm);
            w.WriteAttributeString("Namespace", Namespace);
            var list = datasets.ToList();
            foreach (var d in list)
            {
                w.WriteStartElement("EntityType", edm);
                w.WriteAttributeString("Name", d.Name + "Row");
                w.WriteStartElement("Key", edm);
                w.WriteStartElement("PropertyRef", edm);
                w.WriteAttributeString("Name", d.Columns[0].Name);
                w.WriteEndElement();
                w.WriteEndElement();
                foreach (var c in d.Columns)
                {
                    w.WriteStartElement("Property", edm);
                    w.WriteAttributeString("Name", c.Name);
                    w.WriteAttributeString("Type", c.Type);
                    w.WriteAttributeString("Nullable", c == d.Columns[0] ? "false" : "true");
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            w.WriteStartElement("EntityContainer", edm);
            w.WriteAttributeString("Name", "Container");
            foreach (var d in list)
            {
                w.WriteStartElement("EntitySet", edm);
                w.WriteAttributeString("Name", d.Name);
                w.WriteAttributeString("EntityType", $"{Namespace}.{d.Name}Row");
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        return sb.ToString();
    }

    /// <summary>Reads one page of a dataset for a request's query string. Failures are OData error bodies with a 400.</summary>
    public static async Task<ODataPage> ReadAsync(IDatasetProvider dataset, string baseUrl, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        if (!TryInt(query, "$top", DefaultTop, 1, Dataset<object>.MaxPage, out var top, out var error)) return Failure(error!);
        if (!TryInt(query, "$skip", 0, 0, 10_000_000, out var skip, out error)) return Failure(error!);
        var withCount = query.TryGetValue("$count", out var c) && c.Equals("true", StringComparison.OrdinalIgnoreCase);

        DateTimeOffset? from = null, to = null;
        if (query.TryGetValue("$filter", out var filter) && !string.IsNullOrWhiteSpace(filter))
        {
            var parsed = ParseFilter(filter, dataset.TimeColumn);
            if (parsed.Error is not null) return Failure(parsed.Error);
            (from, to) = (parsed.From, parsed.To);
        }

        IReadOnlyList<string> columns = dataset.Columns.Select(x => x.Name).ToList();
        if (query.TryGetValue("$select", out var select) && !string.IsNullOrWhiteSpace(select))
        {
            var wanted = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            var unknown = wanted.FirstOrDefault(w => !columns.Contains(w));
            if (unknown is not null) return Failure($"The property '{unknown}' does not exist on {dataset.Name}.");
            columns = wanted;
        }

        var page = await dataset.ReadAsync(new DatasetQuery(skip, top + 1, from, to), withCount, ct);
        var more = page.Rows.Count > top;
        var rows = page.Rows.Take(top).Select(r => columns.ToDictionary(col => col, col => Format(r.GetValueOrDefault(col)))).ToList();

        var body = new Dictionary<string, object?> { ["@odata.context"] = $"{baseUrl}/$metadata#{dataset.Name}" };
        if (page.Total is { } total) body["@odata.count"] = total;
        body["value"] = rows;
        if (more) body["@odata.nextLink"] = NextLink(baseUrl, dataset.Name, query, skip + top);
        return new ODataPage(System.Text.Json.JsonSerializer.Serialize(body));
    }

    private static string NextLink(string baseUrl, string set, IReadOnlyDictionary<string, string> query, int nextSkip)
    {
        var pairs = query.Where(kv => kv.Key != "$skip").Append(new KeyValuePair<string, string>("$skip", nextSkip.ToString(CultureInfo.InvariantCulture)));
        return $"{baseUrl}/{set}?" + string.Join('&', pairs.Select(kv => $"{Uri.EscapeDataString(kv.Key).Replace("%24", "$")}={Uri.EscapeDataString(kv.Value)}"));
    }

    private static object? Format(object? v) => v switch
    {
        DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        _ => v,
    };

    private static bool TryInt(IReadOnlyDictionary<string, string> q, string name, int fallback, int min, int max, out int value, out string? error)
    {
        value = fallback;
        error = null;
        if (!q.TryGetValue(name, out var text)) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < min || value > max)
        {
            error = $"{name} must be a whole number from {min} to {max}.";
            return false;
        }

        return true;
    }

    /// <summary>Accepts only <c>Column ge|gt|le|lt dateTime</c> joined with <c>and</c>, on the dataset's time column. Upper bounds are exclusive; "le" and "gt" are adjusted by one tick.</summary>
    public static (DateTimeOffset? From, DateTimeOffset? To, string? Error) ParseFilter(string filter, string? timeColumn)
    {
        if (timeColumn is null) return (null, null, "This dataset cannot be filtered. Load it whole, or page through it with $top and $skip.");
        DateTimeOffset? from = null, to = null;
        foreach (var part in filter.Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (bits.Length != 3 || bits[0] != timeColumn) return (null, null, $"Only filters on {timeColumn} are supported, like {timeColumn} ge 2027-01-01T00:00:00Z.");
            if (!DateTimeOffset.TryParse(bits[2].Trim('\''), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                return (null, null, $"'{bits[2]}' is not a date and time.");
            switch (bits[1].ToLowerInvariant())
            {
                case "ge": from = Max(from, value); break;
                case "gt": from = Max(from, value.AddTicks(1)); break;
                case "lt": to = Min(to, value); break;
                case "le": to = Min(to, value.AddTicks(1)); break;
                default: return (null, null, $"The operator '{bits[1]}' is not supported. Use ge, gt, le or lt.");
            }
        }

        return (from, to, null);
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;

    private static DateTimeOffset Min(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x < b ? x : b;

    private static ODataPage Failure(string message) =>
        new(System.Text.Json.JsonSerializer.Serialize(new { error = new { code = "400", message } }), false, 400);
}
