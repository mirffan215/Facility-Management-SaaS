namespace Fms.Integration.Services;

public sealed class ErpOptions
{
    public const string SectionName = "Erp";

    /// <summary>Lets a tenant choose the built-in fake adapter, which keeps records in memory and never reaches a real system. For development and demonstrations; leave off in production.</summary>
    public bool AllowFakeAdapter { get; set; }

    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a worker may hold an item before another may take it (the worker died or hung).</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);
}
