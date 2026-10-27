using System.Text.Json;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>The rules an administrator may set. Everything else in the lifecycle is fixed (see <see cref="WorkflowDesigner.FixedRules"/>).</summary>
public sealed record WorkflowRulesDto(
    bool RequireApproval, bool RequireVerification, bool RequireCompletionSignature, IReadOnlyList<WorkPriority> WaivedApprovalPriorities, IReadOnlyList<WorkPriority> VerificationPriorities,
    bool RequireTimeOnCompletion, bool RequirePhotoOnCompletion);

public sealed record WorkflowDto(int Version, WorkflowRulesDto Rules, IReadOnlyList<string> FixedRules, DateTimeOffset? PublishedAt, string? Note);

public sealed record WorkflowVersionDto(int Version, WorkflowRulesDto Rules, string Note, Guid? ChangedBy, string? ChangedByName, DateTimeOffset ChangedAt);

public sealed record PublishWorkflowRequest(WorkflowRulesDto? Rules, string? Note, int? ExpectedVersion);

public sealed record RestoreWorkflowRequest(int? Version, string? Note, int? ExpectedVersion);

/// <summary>
/// The workflow designer. Administrators choose which optional steps and evidence an organisation uses; the controls that keep the lifecycle honest cannot be changed here.
/// Every publish is a new numbered version with who, when and why, the previous rules stay readable, and going back is publishing an old version again (never an edit of history).
/// </summary>
public sealed class WorkflowDesigner(WorkDbContext db, Fms.BuildingBlocks.Identity.ICurrentUser user, Fms.BuildingBlocks.Identity.IUserDirectory users, IAuditPort audit, TimeProvider clock)
{
    /// <summary>What no setting can turn off. Shown on the designer so nobody wonders why a switch is missing.</summary>
    public static readonly IReadOnlyList<string> FixedRules =
    [
        "noSelfApproval", "verifierIsNotTheWorker", "checklistBeforeCompletion", "closureCodeAndNotes", "emergenciesSkipApproval", "closedIsLocked", "reasonsForHoldRejectReopenCancel", "everyChangeAudited",
    ];

    public async Task<WorkflowDto> GetAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        var latest = await db.WorkflowVersions.AsNoTracking().OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
        return new WorkflowDto(latest?.Version ?? 0, ToDto(s), FixedRules, latest?.ChangedAt, latest?.Note);
    }

    public async Task<IReadOnlyList<WorkflowVersionDto>> HistoryAsync(CancellationToken ct = default)
    {
        var rows = await db.WorkflowVersions.AsNoTracking().OrderByDescending(v => v.Version).Take(200).ToListAsync(ct);
        var names = await users.GetAsync(rows.Where(r => r.ChangedBy != null).Select(r => r.ChangedBy!.Value), ct);
        return rows.Select(r => new WorkflowVersionDto(r.Version, Parse(r.RulesJson), r.Note, r.ChangedBy, r.ChangedBy is { } u && names.TryGetValue(u, out var p) ? p.DisplayName ?? p.Email : null, r.ChangedAt)).ToList();
    }

    public async Task<Result<WorkflowDto>> PublishAsync(PublishWorkflowRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        if (note is null || note.Length > 500) errors.Add("note", "Say why the rules are changing (at most 500 characters).");
        if (r.Rules is null) errors.Add("rules", "The rules are required.");
        if (r.ExpectedVersion is null) errors.Add("expectedVersion", "Say which version you started from.");
        if (r.Rules is { } rules) Validate(rules, errors);
        if (errors.Any) return errors.ToResult<WorkflowDto>();
        return await ApplyAsync(r.Rules!, note!, r.ExpectedVersion!.Value, ct);
    }

    /// <summary>Publishes an earlier version's rules again as a new version.</summary>
    public async Task<Result<WorkflowDto>> RestoreAsync(RestoreWorkflowRequest r, CancellationToken ct = default)
    {
        if (r.Version is not { } version) return Result<WorkflowDto>.Invalid("version", "Choose a version.");
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        if (note is null || note.Length > 500) return Result<WorkflowDto>.Invalid("note", "Say why you are going back (at most 500 characters).");
        if (r.ExpectedVersion is null) return Result<WorkflowDto>.Invalid("expectedVersion", "Say which version you started from.");
        var old = await db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Version == version, ct);
        if (old is null) return Result<WorkflowDto>.NotFound("That version does not exist.");
        return await ApplyAsync(Parse(old.RulesJson), $"Restored version {version}: {note}", r.ExpectedVersion.Value, ct);
    }

    private async Task<Result<WorkflowDto>> ApplyAsync(WorkflowRulesDto rules, string note, int expected, CancellationToken ct)
    {
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        var current = await db.WorkflowVersions.AsNoTracking().MaxAsync(v => (int?)v.Version, ct) ?? 0;
        if (current != expected) return Result<WorkflowDto>.Conflict($"The rules are now at version {current}; you started from {expected}. Reload and look at what changed.");
        if (s is null)
        {
            s = new WorkSettings();
            db.Settings.Add(s);
        }

        var before = ToDto(s);
        if (JsonSerializer.Serialize(before) == JsonSerializer.Serialize(Normalise(rules))) return Result<WorkflowDto>.Invalid("rules", "Nothing has changed.");

        s.RequireApproval = rules.RequireApproval;
        s.RequireVerification = rules.RequireVerification;
        s.RequireCompletionSignature = rules.RequireCompletionSignature;
        s.WaivedApprovalPriorities = PriorityMask.Of(rules.WaivedApprovalPriorities);
        s.VerificationPriorities = rules.RequireVerification ? PriorityMask.Of(rules.VerificationPriorities) : 0;
        s.RequireTimeOnCompletion = rules.RequireTimeOnCompletion;
        s.RequirePhotoOnCompletion = rules.RequirePhotoOnCompletion;
        var after = ToDto(s);
        var now = clock.GetUtcNow();
        db.WorkflowVersions.Add(new WorkflowVersion { Version = current + 1, RulesJson = JsonSerializer.Serialize(after), Note = note, ChangedBy = user.UserId, ChangedAt = now });
        audit.Record("workflow.published", nameof(WorkSettings), s.Id.ToString(), JsonSerializer.Serialize(new { version = current + 1, from = before, to = after, note }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result<WorkflowDto>.Conflict("Someone else published at the same time. Reload and try again.");
        }

        return Result<WorkflowDto>.Ok(await GetAsync(ct));
    }

    /// <summary>Keeps the history complete when the three simple switches are changed on the older settings screen.</summary>
    public static void Snapshot(WorkDbContext db, WorkSettings s, Guid? by, DateTimeOffset now, int currentVersion, string note) =>
        db.WorkflowVersions.Add(new WorkflowVersion { Version = currentVersion + 1, RulesJson = JsonSerializer.Serialize(ToDto(s)), Note = note, ChangedBy = by, ChangedAt = now });

    private static void Validate(WorkflowRulesDto r, ValidationErrors errors)
    {
        if (r.WaivedApprovalPriorities.Any(p => !Enum.IsDefined(p))) errors.Add("waivedApprovalPriorities", "Unknown priority.");
        if (r.VerificationPriorities.Any(p => !Enum.IsDefined(p))) errors.Add("verificationPriorities", "Unknown priority.");
        if (!r.RequireApproval && r.WaivedApprovalPriorities.Count > 0) errors.Add("waivedApprovalPriorities", "Approval is off for everything, so there is nothing to waive.");
        if (!r.RequireVerification && r.VerificationPriorities.Count > 0) errors.Add("verificationPriorities", "Verification is off for everything, so there is nothing to narrow.");
        if (r.WaivedApprovalPriorities.Distinct().Count() == Enum.GetValues<WorkPriority>().Length) errors.Add("waivedApprovalPriorities", "Waiving every priority is the same as turning approval off. Turn it off instead.");
    }

    private static WorkflowRulesDto Normalise(WorkflowRulesDto r) =>
        new(r.RequireApproval, r.RequireVerification, r.RequireCompletionSignature, PriorityMask.ToList(PriorityMask.Of(r.WaivedApprovalPriorities)),
            r.RequireVerification ? PriorityMask.ToList(PriorityMask.Of(r.VerificationPriorities)) : [], r.RequireTimeOnCompletion, r.RequirePhotoOnCompletion);

    private static WorkflowRulesDto ToDto(WorkSettings? s) => s is null
        ? new WorkflowRulesDto(true, true, false, [], [], false, false)
        : new WorkflowRulesDto(s.RequireApproval, s.RequireVerification, s.RequireCompletionSignature, PriorityMask.ToList(s.WaivedApprovalPriorities), PriorityMask.ToList(s.VerificationPriorities), s.RequireTimeOnCompletion, s.RequirePhotoOnCompletion);

    private static WorkflowRulesDto Parse(string json) => JsonSerializer.Deserialize<WorkflowRulesDto>(json) ?? ToDto(null);
}
