using Fms.Work.Domain;

namespace Fms.Work.Services;

/// <summary>Assembles the offline bundle from the same services (and so the same permission rules) as the screens use.</summary>
public sealed class OfflineJobsService(WorkOrderService orders, WorkEvidenceService evidence, WorkFilesService files, TimeProvider clock)
{
    public const int MaxJobs = 50;

    public async Task<OfflineJobsDto> JobsAsync(CancellationToken ct = default)
    {
        var page = await orders.ListAsync(new WorkOrderQuery(AssignedToMe: true, OpenOnly: true, PageSize: MaxJobs), ct);
        var jobs = new List<OfflineJobDto>();
        foreach (var order in page.Items.Where(o => o.Status is not (WorkOrderStatus.Requested or WorkOrderStatus.Completed or WorkOrderStatus.Verified)))
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
                settings.Value?.CurrencyCode));
        }

        return new OfflineJobsDto(clock.GetUtcNow(), jobs);
    }
}
