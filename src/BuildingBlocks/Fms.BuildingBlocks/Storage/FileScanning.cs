namespace Fms.BuildingBlocks.Storage;

/// <summary>Scans an upload for malware before it is stored. The default implementation does not scan; see docs.</summary>
public interface IFileScanner
{
    Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default);
}

public sealed record ScanOutcome(bool IsClean, string? Threat = null);

/// <summary>Placeholder that accepts everything. Replace with a real scanner (ICAP/ClamAV/Defender) before accepting files from untrusted uploaders.</summary>
public sealed class NoOpFileScanner : IFileScanner
{
    public Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default) => Task.FromResult(new ScanOutcome(true));
}
