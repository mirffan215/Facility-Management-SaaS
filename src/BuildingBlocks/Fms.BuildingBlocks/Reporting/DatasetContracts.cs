using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Fms.BuildingBlocks.Reporting;

/// <summary>A column of a dataset offered to outside tools. <see cref="Type"/> is an OData primitive: Edm.String, Edm.Int32, Edm.Decimal, Edm.Boolean, Edm.Guid, Edm.DateTimeOffset.</summary>
public sealed record DatasetColumn(string Name, string Type);

public sealed record DatasetQuery(int Skip, int Top, DateTimeOffset? From, DateTimeOffset? To);

public sealed record DatasetPage(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, int? Total);

/// <summary>
/// A table a module offers to the BI feed. Datasets are curated: ids, dates, amounts and statuses, never names, contact details or free text typed by people.
/// Reading is always within the caller's tenant (the module's own context applies its filters and row-level security).
/// </summary>
public interface IDatasetProvider
{
    string Name { get; }
    string Permission { get; }
    IReadOnlyList<DatasetColumn> Columns { get; }

    /// <summary>The column a <c>$filter</c> on dates may use (e.g. the day a thing happened), or null when the dataset cannot be filtered.</summary>
    string? TimeColumn { get; }

    Task<DatasetPage> ReadAsync(DatasetQuery query, bool withTotal, CancellationToken ct = default);
}

/// <summary>Builds a dataset from a query, a stable order, an optional date column and a row mapper, so each module only describes its rows.</summary>
public sealed class Dataset<T>(
    string name, string permission, IReadOnlyList<DatasetColumn> columns, Func<IQueryable<T>> source, Expression<Func<T, Guid>> key, Func<T, IReadOnlyDictionary<string, object?>> map,
    string? timeColumn = null, Expression<Func<T, DateTimeOffset>>? time = null) : IDatasetProvider
{
    public const int MaxPage = 1000;

    public string Name => name;
    public string Permission => permission;
    public IReadOnlyList<DatasetColumn> Columns => columns;
    public string? TimeColumn => timeColumn;

    public async Task<DatasetPage> ReadAsync(DatasetQuery q, bool withTotal, CancellationToken ct = default)
    {
        var rows = source();
        if (time is not null)
        {
            if (q.From is { } from) rows = rows.Where(Compare(ExpressionType.GreaterThanOrEqual, from));
            if (q.To is { } to) rows = rows.Where(Compare(ExpressionType.LessThan, to));
        }

        int? total = withTotal ? await rows.CountAsync(ct) : null;
        var page = await rows.OrderBy(key).Skip(Math.Max(0, q.Skip)).Take(Math.Clamp(q.Top, 1, MaxPage + 1)).ToListAsync(ct);
        return new DatasetPage(page.Select(map).ToList(), total);
    }

    private Expression<Func<T, bool>> Compare(ExpressionType op, DateTimeOffset value) =>
        Expression.Lambda<Func<T, bool>>(Expression.MakeBinary(op, time!.Body, Expression.Constant(value)), time.Parameters);
}

public static class DatasetRow
{
    /// <summary>One row from name and value pairs. Enums and dates are given as text so a tool never depends on our numbering.</summary>
    public static IReadOnlyDictionary<string, object?> Of(params (string Name, object? Value)[] cells) => cells.ToDictionary(c => c.Name, c => c.Value);
}

/// <summary>What one module contributes to the BI feed. Each module registers one; the feed collects them all.</summary>
public interface IDatasetCatalog
{
    IReadOnlyList<IDatasetProvider> Datasets { get; }
}

public sealed class DatasetCatalog(IReadOnlyList<IDatasetProvider> datasets) : IDatasetCatalog
{
    public IReadOnlyList<IDatasetProvider> Datasets => datasets;
}
