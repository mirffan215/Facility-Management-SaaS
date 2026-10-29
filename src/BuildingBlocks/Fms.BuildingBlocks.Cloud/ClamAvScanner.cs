using System.Net.Sockets;
using System.Text;
using Fms.BuildingBlocks.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fms.BuildingBlocks.Cloud;

/// <summary>Where uploads are scanned for malware. "None" accepts everything (the default); "ClamAv" streams each upload to a clamd daemon.</summary>
public sealed class ScanningOptions
{
    public const string SectionName = "Scanning";

    public string Provider { get; set; } = "None";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>When the scanner cannot be reached, refuse the upload (true, the safe default) or let it through (false).</summary>
    public bool FailClosed { get; set; } = true;
}

/// <summary>
/// Streams an upload to a ClamAV daemon with its INSTREAM command (length-prefixed chunks, a zero length to end) and reads the one-line answer.
/// Nothing is written to disk and the file is not changed. A scanner that cannot be reached is treated as "not clean" unless the operator chose to fail open.
/// </summary>
public sealed class ClamAvScanner(IOptions<ScanningOptions> options, ILogger<ClamAvScanner> log) : IFileScanner
{
    private const int ChunkSize = 64 * 1024;
    private readonly ScanningOptions _options = options.Value;

    public async Task<ScanOutcome> ScanAsync(byte[] content, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 3, 300)));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_options.Host, _options.Port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), timeout.Token);
            var size = new byte[4];
            for (var offset = 0; offset < content.Length; offset += ChunkSize)
            {
                var length = Math.Min(ChunkSize, content.Length - offset);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(size, length);
                await stream.WriteAsync(size, timeout.Token);
                await stream.WriteAsync(content.AsMemory(offset, length), timeout.Token);
            }

            await stream.WriteAsync(new byte[4], timeout.Token);
            await stream.FlushAsync(timeout.Token);

            var reply = new StringBuilder();
            var buffer = new byte[512];
            int read;
            while (reply.Length < 2048 && (read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                reply.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (reply.ToString().Contains('\0') || reply.ToString().Contains('\n')) break;
            }

            return Interpret(reply.ToString());
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("The malware scanner could not be reached: {Kind}", ex.GetType().Name);
            return Unavailable();
        }
    }

    /// <summary>"stream: OK" is clean, "stream: Name FOUND" is a threat, anything else (an error, a size limit) is not trusted.</summary>
    public ScanOutcome Interpret(string reply)
    {
        var line = reply.Trim('\0', '\n', '\r', ' ');
        if (line.EndsWith(" OK", StringComparison.Ordinal)) return new ScanOutcome(true);
        if (line.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var name = line.Replace("stream:", "", StringComparison.Ordinal).Replace(" FOUND", "", StringComparison.Ordinal).Trim();
            return new ScanOutcome(false, name.Length is > 0 and <= 100 ? name : "malware");
        }

        log.LogWarning("The malware scanner gave an answer that was not understood");
        return Unavailable();
    }

    private ScanOutcome Unavailable() => _options.FailClosed ? new ScanOutcome(false, "scanner unavailable") : new ScanOutcome(true);
}

public static class FileScanningServiceCollectionExtensions
{
    /// <summary>Chooses the upload scanner from configuration. Call before the modules are added: they only add the do-nothing scanner when none is registered.</summary>
    public static IServiceCollection AddFmsFileScanning(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ScanningOptions>().Bind(configuration.GetSection(ScanningOptions.SectionName));
        var provider = configuration.GetSection(ScanningOptions.SectionName)["Provider"];
        if (string.Equals(provider, "ClamAv", StringComparison.OrdinalIgnoreCase)) services.TryAddSingleton<IFileScanner, ClamAvScanner>();
        else if (!string.IsNullOrWhiteSpace(provider) && !string.Equals(provider, "None", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Unknown Scanning:Provider '{provider}'. Use None or ClamAv.");
        return services;
    }
}
