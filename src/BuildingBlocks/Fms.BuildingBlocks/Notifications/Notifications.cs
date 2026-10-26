namespace Fms.BuildingBlocks.Notifications;

/// <summary>
/// One notice for one person. <paramref name="Type"/> and <paramref name="Data"/> let the web app show it in the reader's own language;
/// the email text is bilingual (English then Arabic) because the sender does not know the reader's language.
/// </summary>
public sealed record NotificationRequest(Guid UserId, string Type, object Data, string? LinkPath, string EmailSubject, string EmailBody);

public interface INotificationSink
{
    /// <summary>
    /// Records the notices (in-app, and queued for email) in the caller's tenant. <paramref name="sourceId"/> identifies the event that caused
    /// them (an outbox message id): calling again with the same source and person does nothing, so at-least-once handlers do not double-notify.
    /// Returns how many notices were created.
    /// </summary>
    Task<int> NotifyAsync(Guid sourceId, IReadOnlyCollection<NotificationRequest> requests, CancellationToken ct = default);
}

public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>Sends one email. Throwing means "try again later": the caller is a retrying background job.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
