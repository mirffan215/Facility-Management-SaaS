using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Visitors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Visitors.Services;

public static class VisitorsDatasets
{
    /// <summary>Visits without names, companies, contact details or purposes: who came is not for a BI tool, only when, where and how long.</summary>
    public static IEnumerable<IDatasetProvider> All(VisitorsDbContext db)
    {
        yield return new Dataset<Domain.Visit>("Visits", Permissions.VisitorsReception,
            [new("Id", "Edm.Guid"), new("SiteId", "Edm.Guid"), new("Kind", "Edm.String"), new("Status", "Edm.String"), new("ExpectedAt", "Edm.DateTimeOffset"), new("CheckedInAt", "Edm.DateTimeOffset"),
             new("CheckedOutAt", "Edm.DateTimeOffset"), new("StayMinutes", "Edm.Int32"), new("WatchlistMatch", "Edm.Boolean")],
            () => db.Visits.AsNoTracking(), v => v.Id,
            v => DatasetRow.Of(("Id", v.Id), ("SiteId", v.SiteId), ("Kind", v.Kind.ToString()), ("Status", v.Status.ToString()), ("ExpectedAt", v.ExpectedAt), ("CheckedInAt", v.CheckedInAt), ("CheckedOutAt", v.CheckedOutAt),
                ("StayMinutes", v.CheckedInAt is { } a && v.CheckedOutAt is { } b ? (int)(b - a).TotalMinutes : (int?)null), ("WatchlistMatch", v.WatchlistMatch)),
            "ExpectedAt", v => v.ExpectedAt);
    }
}
