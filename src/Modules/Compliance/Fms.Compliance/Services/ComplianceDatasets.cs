using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Compliance.Services;

public static class ComplianceDatasets
{
    /// <summary>Obligations and permits without titles, descriptions, names or references: when and where, not what people typed.</summary>
    public static IEnumerable<IDatasetProvider> All(ComplianceDbContext db)
    {
        yield return new Dataset<Domain.Obligation>("Obligations", Permissions.ComplianceRead,
            [new("Id", "Edm.Guid"), new("Category", "Edm.String"), new("LocationId", "Edm.Guid"), new("AssetId", "Edm.Guid"), new("IntervalMonths", "Edm.Int32"), new("LastCompletedOn", "Edm.String"),
             new("ValidUntil", "Edm.String"), new("LastResult", "Edm.String"), new("IsArchived", "Edm.Boolean")],
            () => db.Obligations.AsNoTracking(), o => o.Id,
            o => DatasetRow.Of(("Id", o.Id), ("Category", o.Category.ToString()), ("LocationId", o.LocationId), ("AssetId", o.AssetId), ("IntervalMonths", o.IntervalMonths), ("LastCompletedOn", o.LastCompletedOn?.ToString("yyyy-MM-dd")),
                ("ValidUntil", o.ValidUntil?.ToString("yyyy-MM-dd")), ("LastResult", o.LastResult?.ToString()), ("IsArchived", o.IsArchived)),
            null, null);
        yield return new Dataset<Domain.Permit>("Permits", Permissions.PermitsRead,
            [new("Id", "Edm.Guid"), new("Number", "Edm.Int32"), new("TypeCode", "Edm.String"), new("LocationId", "Edm.Guid"), new("Status", "Edm.String"), new("RequestedAt", "Edm.DateTimeOffset"),
             new("PlannedStart", "Edm.DateTimeOffset"), new("PlannedEnd", "Edm.DateTimeOffset"), new("IssuedAt", "Edm.DateTimeOffset"), new("ClosedAt", "Edm.DateTimeOffset")],
            () => db.Permits.AsNoTracking(), p => p.Id,
            p => DatasetRow.Of(("Id", p.Id), ("Number", p.Number), ("TypeCode", p.TypeCode), ("LocationId", p.LocationId), ("Status", p.Status.ToString()), ("RequestedAt", p.RequestedAt), ("PlannedStart", p.PlannedStart),
                ("PlannedEnd", p.PlannedEnd), ("IssuedAt", p.IssuedAt), ("ClosedAt", p.ClosedAt)),
            "RequestedAt", p => p.RequestedAt);
    }
}
