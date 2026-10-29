using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;

namespace Fms.Work.Services;

/// <summary>Assembles the offline bundle from the same services (and so the same permission rules) as the screens use.</summary>
public sealed class OfflineJobsService(WorkOrderService orders, WorkEvidenceService evidence, WorkFilesService files, WorkSetupService setup, TimeProvider clock, IWorkPermitLookup? permits = null)
{
    public const int MaxJobs = 50;

    /// <summary>Permits are included only when the caller may read them (<paramref name="includePermits"/>); a permit's status is as of this download and is never changed from the phone.</summary>
    public async Task<OfflineJobsDto> JobsAsync(bool includePermits = false, CancellationToken ct = default)
    {
        var page = await orders.ListAsync(new WorkOrderQuery(AssignedToMe: true, OpenOnly: true, PageSize: MaxJobs), ct);
        var jobs = new List<OfflineJobDto>();
        var shown = page.Items.Where(o => o.Status is not (WorkOrderStatus.Requested or WorkOrderStatus.Completed or WorkOrderStatus.Verified)).ToList();
        var permitsByOrder = includePermits && permits is not null ? (await permits.ForWorkOrdersAsync(shown.Select(o => o.Id), ct)).ToLookup(p => p.WorkOrderId) : null;
        foreach (var order in shown)
        {
            var detail = (await orders.GetAsync(order.Id, ct)).Value;
            if (detail is null) continue;
            var settings = await evidence.SummaryAsync(order.Id, ct);
            jobs.Add(new OfflineJobDto(
                detail.Order, detail.Events, detail.Comments,
                (await evidence.ChecklistAsync(order.Id, ct)).Value ?? [],
                (await files.SignaturesAsync(order.Id, ct)).Value ?? [],
                (await files.AttachmentsAsync(order.Id, ct)).Value ?? [],
                (await evidence.TimeAsync(order.Id, ct)).Value ?? [],
                (await evidence.CostsAsync(order.Id, ct)).Value ?? [],
                settings.Value?.CurrencyCode,
                permitsByOrder?[order.Id].ToList()));
        }

        return new OfflineJobsDto(clock.GetUtcNow(), jobs, (await setup.GetSettingsAsync(ct)).RequireCompletionSignature);
    }
}
