namespace Fms.Work.Domain;

/// <summary>What is known about one person who could take a job, from the work orders alone.</summary>
public sealed record CandidateStats(Guid UserId, string Name, int SameAsset, int SameCategory, int SamePlace, int OpenJobs);

public sealed record RankedCandidate(CandidateStats Stats, int Score);

public sealed record VendorStats(Guid VendorId, string Name, string ContractNumber, int? ResolutionMinutes, int PastJobs);

/// <summary>
/// Who should do this job. Plain arithmetic over history, so it can be tested and explained: work on the same asset counts most, then the same kind of work, then the same place;
/// every job already open on someone's plate counts against them. No model, and nothing is decided: the dispatcher still chooses.
/// </summary>
public static class AssignmentRanker
{
    public const int AssetWeight = 5;
    public const int CategoryWeight = 3;
    public const int PlaceWeight = 1;
    public const int LoadPenalty = 2;

    public static int Score(CandidateStats c) => AssetWeight * c.SameAsset + CategoryWeight * c.SameCategory + PlaceWeight * c.SamePlace - LoadPenalty * c.OpenJobs;

    /// <summary>The best candidates first. Someone with no history and an empty plate ranks above someone busy with no history; ties go to the lighter load, then to the name.</summary>
    public static IReadOnlyList<RankedCandidate> Rank(IEnumerable<CandidateStats> candidates, int max = 3) =>
        candidates.Select(c => new RankedCandidate(c, Score(c)))
            .OrderByDescending(r => r.Score).ThenBy(r => r.Stats.OpenJobs).ThenBy(r => r.Stats.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Stats.UserId)
            .Take(Math.Clamp(max, 1, 10)).ToList();

    /// <summary>Vendors with a contract target for this priority first, the quickest resolution before the slower; then the ones with more past jobs of this kind; then by name.</summary>
    public static IReadOnlyList<VendorStats> RankVendors(IEnumerable<VendorStats> vendors, int max = 3) =>
        vendors.OrderBy(v => v.ResolutionMinutes is null).ThenBy(v => v.ResolutionMinutes ?? int.MaxValue).ThenByDescending(v => v.PastJobs).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.VendorId)
            .Take(Math.Clamp(max, 1, 10)).ToList();
}
