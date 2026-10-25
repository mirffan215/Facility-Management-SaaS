using System.IO.Compression;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Facilities.Domain;

public enum DocumentKind { Manual = 1, Warranty = 2, Certificate = 3, Photo = 4, Drawing = 5, Other = 6 }

/// <summary>A file attached to an asset. The bytes live in file storage under a key built only from ids; this row holds the metadata.</summary>
public sealed class AssetDocument : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public DocumentKind Kind { get; set; }

    /// <summary>Display name only (sanitised, never used in a storage path).</summary>
    public string FileName { get; set; } = "";

    /// <summary>The type detected from the file's content, not the one the client claimed.</summary>
    public string ContentType { get; set; } = "";

    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }

    /// <summary>Soft delete: the file is hidden from lists and downloads, but the stored bytes and this row are kept as evidence.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

public sealed record DetectedFile(string ContentType, string Extension);

/// <summary>
/// Decides what a file really is from its bytes. Only an allow-list of passive document and image formats is accepted; anything
/// that can carry script or code (HTML, SVG, executables, macro-enabled Office files, archives) is refused.
/// </summary>
public static class FileSniffer
{
    private const int MaxZipEntries = 2000;
    private const long MaxZipUncompressedBytes = 200L * 1024 * 1024;

    public static DetectedFile? Detect(byte[] bytes)
    {
        if (bytes.Length < 4) return null;

        if (StartsWith(bytes, "%PDF-"u8)) return new("application/pdf", ".pdf");
        if (StartsWith(bytes, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return new("image/png", ".png");
        if (StartsWith(bytes, [0xFF, 0xD8, 0xFF])) return new("image/jpeg", ".jpg");
        if (bytes.Length >= 12 && StartsWith(bytes, "RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return new("image/webp", ".webp");
        if (StartsWith(bytes, [0x50, 0x4B, 0x03, 0x04])) return DetectOoxml(bytes);
        return IsPlainText(bytes) ? new("text/plain", ".txt") : null;
    }

    private static DetectedFile? DetectOoxml(byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count is 0 or > MaxZipEntries) return null;

            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                total += entry.Length;
                if (total > MaxZipUncompressedBytes) return null;
                var name = entry.FullName;
                if (name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal) || name.Contains('\\')) return null;
                if (name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) || name.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("xl/embeddings/", StringComparison.OrdinalIgnoreCase)) return null;
                names.Add(name);
            }

            if (!names.Contains("[Content_Types].xml")) return null;
            if (names.Contains("word/document.xml")) return new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx");
            if (names.Contains("xl/workbook.xml")) return new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx");
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>UTF-8 text with no NULs or stray control characters (tab, CR and LF allowed). Checked on the first 64 KB.</summary>
    private static bool IsPlainText(byte[] bytes)
    {
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, 64 * 1024));
        if (sample.Contains((byte)0)) return false;
        try
        {
            // flush only when the sample is the whole file: a cut in the middle of a multi-byte character is not an error.
            var chars = new char[sample.Length];
            new System.Text.UTF8Encoding(false, true).GetDecoder()
                .Convert(sample, chars, bytes.Length <= sample.Length, out _, out var charsUsed, out _);
            return chars.AsSpan(0, charsUsed).ToArray().All(c => !char.IsControl(c) || c is '\t' or '\r' or '\n');
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);
}

public static class DocumentNames
{
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>
    /// A safe display name: no path parts, no control or bidirectional-override characters, limited length, and an extension that
    /// matches the detected type (so "photo.html" holding a PNG is shown as "photo.html.png", never as something executable).
    /// </summary>
    public static string Sanitize(string? original, string detectedExtension)
    {
        var name = original ?? "";
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0) name = name[(cut + 1)..];

        name = new string(name.Where(c => !char.IsControl(c) && c is not ('‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩'))
            .Select(c => Array.IndexOf(Forbidden, c) >= 0 ? '_' : c).ToArray()).Trim().TrimStart('.');
        if (name.Length == 0) name = "file";

        var matches = string.Equals(Path.GetExtension(name), detectedExtension, StringComparison.OrdinalIgnoreCase)
                      || (detectedExtension == ".jpg" && string.Equals(Path.GetExtension(name), ".jpeg", StringComparison.OrdinalIgnoreCase));
        if (!matches) name += detectedExtension;

        const int max = 200;
        return name.Length <= max ? name : Path.GetFileNameWithoutExtension(name)[..(max - detectedExtension.Length)] + detectedExtension;
    }
}
