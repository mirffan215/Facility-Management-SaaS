using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.Platform.Domain;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Notifications;

public sealed record NotificationDto(Guid Id, string Type, JsonElement Data, string? LinkPath, DateTimeOffset CreatedAt, bool IsRead);

public sealed record EmailMessagePayload(string To, string Subject, string Body);

/// <summary>Creates in-app notices and queues their emails, in the same transaction, once per event and person.</summary>
public sealed class NotificationSink(PlatformDbContext db, IOutbox outbox, TimeProvider clock) : INotificationSink
{
    public const string EmailMessageType = "email.send";

    public async Task<int> NotifyAsync(Guid sourceId, IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default)
    {
        var wanted = requests.GroupBy(r => r.UserId).Select(g => g.First()).ToList();
        if (wanted.Count == 0) return 0;

        var ids = wanted.Select(r => r.UserId).ToList();
        var already = (await db.Notifications.AsNoTracking().Where(n => n.SourceId == sourceId && ids.Contains(n.UserId)).Select(n => n.UserId).ToListAsync(ct)).ToHashSet();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive).ToDictionaryAsync(u => u.Id, ct);

        var now = clock.GetUtcNow();
        var created = 0;
        foreach (var r in wanted.Where(r => !already.Contains(r.UserId) && users.ContainsKey(r.UserId)))
        {
            db.Notifications.Add(new Notification
            {
                UserId = r.UserId, Type = r.Type, DataJson = JsonSerializer.Serialize(r.Data, JsonOptions), LinkPath = r.LinkPath, SourceId = sourceId, CreatedAt = now,
            });
            if (users[r.UserId].Email is { Length: > 0 } email) outbox.Enqueue(EmailMessageType, new EmailMessagePayload(email, r.EmailSubject, r.EmailBody));
            created++;
        }

        if (created == 0) return 0;
        try
        {
            await db.SaveChangesAsync(ct);
            return created;
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear(); // a concurrent delivery of the same event got there first
            return 0;
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}

public sealed class EmailSendHandler(IEmailSender sender) : IOutboxHandler
{
    public string MessageType => NotificationSink.EmailMessageType;

    public Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<EmailMessagePayload>();
        return sender.SendAsync(new EmailMessage(m.To, m.Subject, m.Body), ct);
    }
}

/// <summary>A person's own notices. Nothing here can reach another person's: every query is for the signed-in user.</summary>
public sealed class NotificationService(PlatformDbContext db, ICurrentUser user, TimeProvider clock)
{
    public async Task<Page<NotificationDto>> ListAsync(bool unreadOnly, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<NotificationDto>.Normalize(page, pageSize);
        if (user.UserId is not { } me) return new Page<NotificationDto>([], 0, number, size);

        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == me);
        if (unreadOnly) query = query.Where(n => n.ReadAt == null);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(n => n.CreatedAt).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<NotificationDto>(items.Select(n => new NotificationDto(n.Id, n.Type, JsonDocument.Parse(n.DataJson).RootElement.Clone(), n.LinkPath, n.CreatedAt, n.ReadAt is not null)).ToList(), total, number, size);
    }

    public async Task<int> UnreadCountAsync(CancellationToken ct = default) =>
        user.UserId is { } me ? await db.Notifications.CountAsync(n => n.UserId == me && n.ReadAt == null, ct) : 0;

    public async Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return false;
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me, ct);
        if (n is null) return false;
        n.ReadAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return 0;
        var now = clock.GetUtcNow();
        return await db.Notifications.Where(n => n.UserId == me && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }
}
