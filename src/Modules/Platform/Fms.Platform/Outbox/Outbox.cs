using System.Text.Json;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Persistence;

namespace Fms.Platform.Outbox;

/// <summary>A durable message written in the same transaction as the business change that caused it.</summary>
public sealed class OutboxMessage : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? LastError { get; set; }
}

public interface IOutbox
{
    /// <summary>
    /// Adds a message to the current unit of work. It is stored when (and only when) the caller's SaveChanges commits, and is
    /// delivered at least once to the handler registered for <paramref name="type"/>, in the sender's tenant.
    /// </summary>
    void Enqueue(string type, object payload);
}

public sealed class Outbox(PlatformDbContext db, TimeProvider clock) : IOutbox
{
    public void Enqueue(string type, object payload)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 100) throw new ArgumentException("Message type is required (max 100).", nameof(type));

        var now = clock.GetUtcNow();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = now,
            AvailableAt = now,
        });
    }
}

/// <summary>What a handler receives. Delivery is at least once, so handlers must be idempotent; use <see cref="MessageId"/> to dedupe.</summary>
public sealed record OutboxMessageContext(Guid MessageId, Guid TenantId, string Type, string PayloadJson, int Attempt)
{
    public T Payload<T>() => JsonSerializer.Deserialize<T>(PayloadJson) ?? throw new InvalidOperationException("Message payload is empty.");
}

/// <summary>Handles one message type. Runs in a DI scope whose tenant is the message's tenant.</summary>
public interface IOutboxHandler
{
    string MessageType { get; }
    Task HandleAsync(OutboxMessageContext message, CancellationToken ct);
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(14);
}
