namespace Fms.BuildingBlocks.Identity;

/// <summary>A category a request can be filed under.</summary>
public sealed record AiCategory(Guid Id, string Code, string Name);

/// <summary>The words of a work order, for a summary. Names of people are not included.</summary>
public sealed record AiWorkText(Guid Id, string Number, string Title, string? Description, string Status, string Priority, string? CategoryName, string? LocationName, string? AssetName, DateTimeOffset RequestedAt);

/// <summary>One step in a work order's story: a status change or a comment, with the words used.</summary>
public sealed record AiWorkStep(DateTimeOffset At, string Kind, string? Text);

/// <summary>What the AI module may ask the Work module. Answered for the current tenant only.</summary>
public interface IWorkAiSource
{
    Task<IReadOnlyList<AiCategory>> CategoriesAsync(CancellationToken ct = default);

    Task<AiWorkText?> GetAsync(Guid workOrderId, CancellationToken ct = default);

    /// <summary>The newest steps first, at most the number asked for.</summary>
    Task<IReadOnlyList<AiWorkStep>> StepsAsync(Guid workOrderId, int max, CancellationToken ct = default);
}
