using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Security;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

/// <summary>Tells the right people about a request: approvers when one is waiting, the requester when it is decided or ordered. At-least-once safe: the sink ignores a repeat for the same person.</summary>
public sealed class PurchasingEventHandler(PurchasingDbContext db, IUserDirectory users, INotificationSink sink) : IOutboxHandler
{
    public string MessageType => PurchasingMessages.Event;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<PurchasingEventMessage>();
        var r = await db.Requests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == m.RequestId, ct);
        if (r is null) return;

        IEnumerable<Guid> recipients;
        string en, ar;
        switch (m.Event)
        {
            case PurchasingEvent.Submitted:
                var levels = new[] { Permissions.PurchasingApprove1, Permissions.PurchasingApprove2, Permissions.PurchasingApprove3 }.Skip(r.RequiredLevel - 1);
                var approvers = new HashSet<Guid>();
                foreach (var permission in levels) foreach (var u in await users.WithPermissionAsync(permission, ct)) approvers.Add(u.Id);
                recipients = approvers.Where(a => a != r.RequestedBy);
                (en, ar) = ("is waiting for your approval", "بانتظار اعتمادك");
                break;
            case PurchasingEvent.Approved:
                recipients = [r.RequestedBy];
                (en, ar) = ("was approved", "تم اعتماده");
                break;
            case PurchasingEvent.Rejected:
                recipients = [r.RequestedBy];
                (en, ar) = ("was rejected", "تم رفضه");
                break;
            default:
                recipients = [r.RequestedBy];
                (en, ar) = ("has been ordered", "تم إصدار أمر شراء له");
                break;
        }

        var display = PurchasingRules.DisplayRequest(r.Number);
        var body = $"{display} · {r.Title}: {en}.\n\n{display} · {r.Title}: {ar}.";
        await sink.NotifyAsync(message.MessageId, recipients.Where(x => x != m.ActorId).Distinct().Select(u => new NotificationRequest(
            u, "purchase." + m.Event, new { number = display, title = r.Title, requestId = r.Id }, $"/purchasing?tab=requests&request={r.Id}", $"{display} {en}", body)).ToList(), ct);
    }
}
