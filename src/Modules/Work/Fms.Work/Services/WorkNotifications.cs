using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>Who should hear about what. Pure, so the rules are tested without a database.</summary>
public static class NotificationPlanner
{
    public sealed record People(IReadOnlyCollection<Guid> Approvers, IReadOnlyCollection<Guid> Dispatchers, IReadOnlyCollection<Guid> Verifiers);

    public static IReadOnlyList<Guid> Recipients(WorkOrderAction action, WorkOrder wo, Guid? actor, People people)
    {
        IEnumerable<Guid> who = action switch
        {
            WorkOrderAction.Created => wo.Status == WorkOrderStatus.Requested ? people.Approvers : people.Dispatchers,
            WorkOrderAction.Approved => people.Dispatchers.Append(wo.RequestedBy),
            WorkOrderAction.Rejected => [wo.RequestedBy],
            WorkOrderAction.Assigned => Assignee(wo).Append(wo.RequestedBy),
            WorkOrderAction.Started or WorkOrderAction.Resumed => [wo.RequestedBy],
            WorkOrderAction.Held => people.Dispatchers.Append(wo.RequestedBy),
            WorkOrderAction.Completed => people.Verifiers.Append(wo.RequestedBy),
            WorkOrderAction.Closed => [wo.RequestedBy],
            WorkOrderAction.Reopened or WorkOrderAction.Cancelled => Assignee(wo).Append(wo.RequestedBy),
            _ => [],
        };
        return who.Where(g => g != actor).Distinct().ToList();

        static IEnumerable<Guid> Assignee(WorkOrder w) => w.AssignedTo is { } a ? [a] : [];
    }

    public static IReadOnlyList<Guid> SlaRecipients(WorkOrder wo, People people) =>
        people.Dispatchers.Concat(wo.AssignedTo is { } a ? [a] : []).Distinct().ToList();
}

/// <summary>The words. English and Arabic in one message, since the sender does not know the reader's language.</summary>
public static class NoticeText
{
    private static readonly Dictionary<WorkOrderAction, (string En, string Ar)> Actions = new()
    {
        [WorkOrderAction.Created] = ("was requested", "تم طلبه"),
        [WorkOrderAction.Approved] = ("was approved", "تم اعتماده"),
        [WorkOrderAction.Rejected] = ("was rejected", "تم رفضه"),
        [WorkOrderAction.Assigned] = ("was assigned", "تم إسناده"),
        [WorkOrderAction.Started] = ("has started", "بدأ العمل عليه"),
        [WorkOrderAction.Held] = ("was put on hold", "تم تعليقه"),
        [WorkOrderAction.Resumed] = ("has resumed", "استؤنف العمل عليه"),
        [WorkOrderAction.Completed] = ("was completed", "تم إكماله"),
        [WorkOrderAction.Closed] = ("was closed", "تم إغلاقه"),
        [WorkOrderAction.Reopened] = ("was reopened", "أُعيد فتحه"),
        [WorkOrderAction.Cancelled] = ("was cancelled", "تم إلغاؤه"),
    };

    private static readonly Dictionary<SlaAlert, (string En, string Ar)> Alerts = new()
    {
        [SlaAlert.ResponseBreached] = ("missed its response time", "تجاوز مهلة الاستجابة"),
        [SlaAlert.ResolutionAtRisk] = ("is close to missing its resolution time", "يقترب من تجاوز مهلة الحل"),
        [SlaAlert.ResolutionBreached] = ("missed its resolution time", "تجاوز مهلة الحل"),
    };

    public static NotificationRequest For(Guid userId, WorkOrderAction action, WorkOrder wo, string? reason)
    {
        var (en, ar) = Actions.GetValueOrDefault(action, ("was updated", "تم تحديثه"));
        var number = WorkOrderRules.DisplayNumber(wo.Number);
        return new NotificationRequest(userId, "workorder." + action, new { workOrderId = wo.Id, number, title = wo.Title, reason }, $"/work-orders/{wo.Id}",
            $"{number} {en}",
            Body(number, wo.Title, $"{number} {en}", $"{number} {ar}", reason));
    }

    public static NotificationRequest ForSla(Guid userId, SlaAlert alert, WorkOrder wo)
    {
        var (en, ar) = Alerts[alert];
        var number = WorkOrderRules.DisplayNumber(wo.Number);
        return new NotificationRequest(userId, "workorder.sla." + alert, new { workOrderId = wo.Id, number, title = wo.Title }, $"/work-orders/{wo.Id}",
            $"{number} {en}",
            Body(number, wo.Title, $"{number} {en}", $"{number} {ar}", null));
    }

    private static string Body(string number, string title, string en, string ar, string? reason) =>
        $"{en}\n{title}{(reason is null ? "" : $"\nReason: {reason}")}\n\n{ar}\n{title}{(reason is null ? "" : $"\nالسبب: {reason}")}\n";
}

/// <summary>Turns lifecycle events into notices for the right people. At-least-once safe: the sink ignores a repeated event for the same person.</summary>
public sealed class WorkOrderEventHandler(WorkDbContext db, IUserDirectory users, INotificationSink sink) : IOutboxHandler
{
    public string MessageType => WorkMessages.Event;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<WorkOrderEventMessage>();
        var wo = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.Id == m.WorkOrderId, ct);
        if (wo is null) return;

        var people = await PeopleAsync(users, ct);
        var recipients = NotificationPlanner.Recipients(m.Action, wo, m.ActorId, people);
        await sink.NotifyAsync(message.MessageId, recipients.Select(r => NoticeText.For(r, m.Action, wo, m.Reason)).ToList(), ct);
    }

    internal static async Task<NotificationPlanner.People> PeopleAsync(IUserDirectory users, CancellationToken ct) =>
        new((await users.WithPermissionAsync(Permissions.WorkOrdersApprove, ct)).Select(u => u.Id).ToList(),
            (await users.WithPermissionAsync(Permissions.WorkOrdersAssign, ct)).Select(u => u.Id).ToList(),
            (await users.WithPermissionAsync(Permissions.WorkOrdersVerify, ct)).Select(u => u.Id).ToList());
}

public sealed class SlaAlertHandler(WorkDbContext db, IUserDirectory users, INotificationSink sink) : IOutboxHandler
{
    public string MessageType => WorkMessages.Sla;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<SlaAlertMessage>();
        var wo = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.Id == m.WorkOrderId, ct);
        if (wo is null) return;

        var people = await WorkOrderEventHandler.PeopleAsync(users, ct);
        await sink.NotifyAsync(message.MessageId, NotificationPlanner.SlaRecipients(wo, people).Select(r => NoticeText.ForSla(r, m.Alert, wo)).ToList(), ct);
    }
}
