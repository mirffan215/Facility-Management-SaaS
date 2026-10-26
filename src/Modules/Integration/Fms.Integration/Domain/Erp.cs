using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Integration.Domain;

/// <summary>Whether and how a tenant connects to its accounting system. Off until someone turns it on. Credentials are never stored here: they live in the secret store.</summary>
public sealed class ErpSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public bool Enabled { get; set; }

    /// <summary>Which adapter to use, by name.</summary>
    public string Adapter { get; set; } = "";

    /// <summary>The company (legal entity) in the accounting system the data goes to, for example "USMF" in Dynamics 365 Finance.</summary>
    public string CompanyId { get; set; } = "";

    public DateTimeOffset? EnabledAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum ExportStatus
{
    /// <summary>Waiting to be sent (also: failed and waiting for its next try).</summary>
    Pending = 1,

    /// <summary>Handed to the adapter; a worker holds it.</summary>
    Sending = 2,
    Succeeded = 3,

    /// <summary>Gave up (too many tries, or an error a retry cannot fix). A person decides: fix the cause and retry.</summary>
    DeadLetter = 4,

    /// <summary>A newer version of the same thing was queued before this one was sent, so sending it would only be stale.</summary>
    Superseded = 5,
}

public enum ReconcileState { NotChecked = 0, Matched = 1, Mismatch = 2, Missing = 3 }

/// <summary>One thing to send to the accounting system, with everything needed to send it again exactly the same way.</summary>
public sealed class ErpExportItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public ErpEntity Entity { get; set; }
    public Guid EntityId { get; set; }
    public long Revision { get; set; }

    /// <summary>"Vendor:{id}:{revision}". The accounting system must treat a repeat of the same key as the same request.</summary>
    public string IdempotencyKey { get; set; } = "";

    public string PayloadJson { get; set; } = "{}";
    public ExportStatus Status { get; set; } = ExportStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public string? ErpReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SucceededAt { get; set; }
    public ReconcileState Reconcile { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public string? ReconcileDetail { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>When to try again: doubling from a base delay, up to a ceiling. Free of the database so it can be tested exhaustively.</summary>
public static class ErpRetry
{
    public static TimeSpan Delay(int attemptsSoFar, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        var factor = 1L << Math.Clamp(attemptsSoFar - 1, 0, 20);
        return TimeSpan.FromTicks(Math.Min(maxDelay.Ticks, baseDelay.Ticks * factor));
    }

    public static bool GiveUp(int attemptsSoFar, int maxAttempts) => attemptsSoFar >= maxAttempts;
}
