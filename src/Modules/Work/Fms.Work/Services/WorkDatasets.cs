using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>Work tables for outside tools: ids, dates, amounts and statuses. No titles, descriptions or comments (people type personal details into those).</summary>
public static class WorkDatasets
{
    private const string Guid_ = "Edm.Guid", Str = "Edm.String", Int = "Edm.Int32", Dec = "Edm.Decimal", Dto = "Edm.DateTimeOffset", Bool = "Edm.Boolean";

    public static IEnumerable<IDatasetProvider> All(WorkDbContext db)
    {
        yield return new Dataset<Domain.WorkOrder>("WorkOrders", Permissions.WorkOrdersRead,
            [new("Id", Guid_), new("Number", Int), new("Type", Str), new("Priority", Str), new("Status", Str), new("LocationId", Guid_), new("AssetId", Guid_), new("CategoryId", Guid_), new("AssignedVendorId", Guid_),
             new("RequestedAt", Dto), new("StartedAt", Dto), new("CompletedAt", Dto), new("VerifiedAt", Dto), new("ClosedAt", Dto), new("DueAt", Dto), new("ResponseDueAt", Dto), new("RespondedAt", Dto),
             new("ResolutionDueAt", Dto), new("ClosureCode", Str)],
            () => db.WorkOrders.AsNoTracking(), w => w.Id,
            w => DatasetRow.Of(("Id", w.Id), ("Number", w.Number), ("Type", w.Type.ToString()), ("Priority", w.Priority.ToString()), ("Status", w.Status.ToString()), ("LocationId", w.LocationId), ("AssetId", w.AssetId),
                ("CategoryId", w.CategoryId), ("AssignedVendorId", w.AssignedVendorId), ("RequestedAt", w.RequestedAt), ("StartedAt", w.StartedAt), ("CompletedAt", w.CompletedAt), ("VerifiedAt", w.VerifiedAt),
                ("ClosedAt", w.ClosedAt), ("DueAt", w.DueAt), ("ResponseDueAt", w.ResponseDueAt), ("RespondedAt", w.RespondedAt), ("ResolutionDueAt", w.ResolutionDueAt), ("ClosureCode", w.ClosureCode?.ToString())),
            "RequestedAt", w => w.RequestedAt);

        var voidedCosts = db.Voids.Where(v => v.EntryType == Domain.LedgerEntryType.Cost).Select(v => v.EntryId);
        yield return new Dataset<Domain.WorkOrderCostLine>("WorkCosts", Permissions.WorkOrdersRead,
            [new("Id", Guid_), new("WorkOrderId", Guid_), new("Kind", Str), new("Quantity", Dec), new("UnitCost", Dec), new("Amount", Dec), new("RecordedAt", Dto)],
            () => db.CostLines.AsNoTracking().Where(c => !voidedCosts.Contains(c.Id)), c => c.Id,
            c => DatasetRow.Of(("Id", c.Id), ("WorkOrderId", c.WorkOrderId), ("Kind", c.Kind.ToString()), ("Quantity", c.Quantity), ("UnitCost", c.UnitCost), ("Amount", Math.Round(c.Quantity * c.UnitCost, 2)), ("RecordedAt", c.RecordedAt)),
            "RecordedAt", c => c.RecordedAt);

        var voidedTime = db.Voids.Where(v => v.EntryType == Domain.LedgerEntryType.Time).Select(v => v.EntryId);
        yield return new Dataset<Domain.WorkOrderTimeEntry>("WorkTime", Permissions.WorkOrdersRead,
            [new("Id", Guid_), new("WorkOrderId", Guid_), new("WorkedOn", Str), new("Minutes", Int), new("HourlyRate", Dec), new("Cost", Dec), new("RecordedAt", Dto)],
            () => db.TimeEntries.AsNoTracking().Where(t => !voidedTime.Contains(t.Id)), t => t.Id,
            t => DatasetRow.Of(("Id", t.Id), ("WorkOrderId", t.WorkOrderId), ("WorkedOn", t.WorkedOn.ToString("yyyy-MM-dd")), ("Minutes", t.Minutes), ("HourlyRate", t.HourlyRate), ("Cost", Math.Round(t.Minutes * t.HourlyRate / 60m, 2)), ("RecordedAt", t.RecordedAt)),
            "RecordedAt", t => t.RecordedAt);

        yield return new Dataset<Domain.PmOccurrence>("PmOccurrences", Permissions.PmRead,
            [new("Id", Guid_), new("ScheduleId", Guid_), new("AssetId", Guid_), new("WorkOrderId", Guid_), new("DueOn", Str), new("Trigger", Str), new("Outcome", Str), new("FinishedOn", Str), new("OnTime", Bool), new("GeneratedAt", Dto)],
            () => db.PmOccurrences.AsNoTracking(), o => o.Id,
            o => DatasetRow.Of(("Id", o.Id), ("ScheduleId", o.ScheduleId), ("AssetId", o.AssetId), ("WorkOrderId", o.WorkOrderId), ("DueOn", o.DueOn.ToString("yyyy-MM-dd")), ("Trigger", o.Trigger.ToString()),
                ("Outcome", o.Outcome.ToString()), ("FinishedOn", o.FinishedOn?.ToString("yyyy-MM-dd")), ("OnTime", o.OnTime), ("GeneratedAt", o.GeneratedAt)),
            "GeneratedAt", o => o.GeneratedAt);
    }
}
