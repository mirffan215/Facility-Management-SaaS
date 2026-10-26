using System.Security.Cryptography;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Storage;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fms.Facilities.Services;

public sealed record DocumentDto(
    Guid Id, Guid AssetId, DocumentKind Kind, string FileName, string ContentType, long SizeBytes, string Sha256, string? Description,
    DateTimeOffset UploadedAt, Guid? UploadedBy, bool IsDeleted)
{
    public static DocumentDto From(AssetDocument d) =>
        new(d.Id, d.AssetId, d.Kind, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.Description, d.UploadedAt, d.UploadedBy, d.DeletedAt is not null);
}

public sealed record DownloadedDocument(DocumentDto Document, Stream Content);

public sealed record DeleteDocumentRequest(string? Reason);

public sealed class DocumentService(
    FacilitiesDbContext db, IFileStore files, IFileScanner scanner, IAuditPort audit, ICurrentUser user, IOptions<FacilitiesOptions> options, TimeProvider clock)
{
    private const string StorageKind = "asset-documents";

    public async Task<Result<IReadOnlyList<DocumentDto>>> ListAsync(Guid assetId, CancellationToken ct = default)
    {
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) return Result<IReadOnlyList<DocumentDto>>.NotFound("Asset not found.");
        var docs = await db.AssetDocuments.AsNoTracking().Where(d => d.AssetId == assetId && d.DeletedAt == null).OrderByDescending(d => d.UploadedAt).ToListAsync(ct);
        return Result<IReadOnlyList<DocumentDto>>.Ok(docs.Select(DocumentDto.From).ToList());
    }

    public async Task<Result<DocumentDto>> UploadAsync(Guid assetId, DocumentKind kind, string? description, string? originalFileName, Stream content, CancellationToken ct = default)
    {
        var o = options.Value;
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return Result<DocumentDto>.NotFound("Asset not found.");
        if (asset.Status == AssetStatus.Retired) return Result<DocumentDto>.Conflict("A retired asset is a closed record; files cannot be added.");

        var errors = new ValidationErrors();
        if (!Enum.IsDefined(kind)) errors.Add("kind", "Unknown document kind.");
        var note = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (note is { Length: > 500 }) errors.Add("description", "Description is at most 500 characters.");
        if (errors.Any) return errors.ToResult<DocumentDto>();

        if (await db.AssetDocuments.CountAsync(d => d.AssetId == assetId && d.DeletedAt == null, ct) >= o.MaxDocumentsPerAsset)
            return Result<DocumentDto>.Conflict($"An asset can have at most {o.MaxDocumentsPerAsset} files.");

        var bytes = await ReadLimitedAsync(content, o.MaxDocumentBytes, ct);
        if (bytes is null) return new Result<DocumentDto>(ResultStatus.TooLarge, Message: $"Files can be at most {o.MaxDocumentBytes / (1024 * 1024)} MB.");
        if (bytes.Length == 0) return Result<DocumentDto>.Invalid("file", "The file is empty.");

        var detected = FileSniffer.Detect(bytes);
        if (detected is null)
            return new Result<DocumentDto>(ResultStatus.UnsupportedType, Message: "This file type is not allowed. Upload a PDF, PNG, JPEG, WebP, Word (.docx), Excel (.xlsx) or plain-text file.");

        var scan = await scanner.ScanAsync(bytes, ct);
        if (!scan.IsClean) return Result<DocumentDto>.Invalid("file", "The file was rejected by the security scan.");

        var id = Guid.NewGuid();
        var document = new AssetDocument
        {
            Id = id, AssetId = assetId, Kind = kind, FileName = DocumentNames.Sanitize(originalFileName, detected.Extension), ContentType = detected.ContentType,
            SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Description = note,
            UploadedAt = clock.GetUtcNow(), UploadedBy = user.UserId,
        };

        // Bytes first: a failed save then leaves an unreferenced (harmless, write-once) object rather than a row pointing at nothing.
        await using (var stream = new MemoryStream(bytes))
        {
            document.StorageKey = await files.PutAsync(StorageKind, id, "content", stream, ct);
        }

        db.AssetDocuments.Add(document);
        audit.Record("document.uploaded", nameof(AssetDocument), id.ToString(),
            JsonSerializer.Serialize(new { assetId, document.FileName, document.ContentType, document.SizeBytes, document.Sha256, document.Kind }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await files.DeleteAsync(document.StorageKey, CancellationToken.None);
            throw;
        }

        return Result<DocumentDto>.Ok(DocumentDto.From(document));
    }

    /// <summary>Opens a file for download. Every download is audited: who looked at which contract or certificate, and when.</summary>
    public async Task<Result<DownloadedDocument>> DownloadAsync(Guid documentId, CancellationToken ct = default)
    {
        var document = await db.AssetDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (document is null) return Result<DownloadedDocument>.NotFound();

        var stream = await files.GetAsync(document.StorageKey, ct);
        if (stream is null) return Result<DownloadedDocument>.NotFound("The stored file is missing.");

        audit.Record("document.downloaded", nameof(AssetDocument), documentId.ToString(), JsonSerializer.Serialize(new { document.AssetId, document.FileName }));
        await db.SaveChangesAsync(ct);
        return Result<DownloadedDocument>.Ok(new DownloadedDocument(DocumentDto.From(document), stream));
    }

    public async Task<Result<DocumentDto>> DeleteAsync(Guid documentId, DeleteDocumentRequest r, CancellationToken ct = default)
    {
        var document = await db.AssetDocuments.FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (document is null) return Result<DocumentDto>.NotFound();
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim();
        if (reason is null || reason.Length > 500) return Result<DocumentDto>.Invalid("reason", "A reason is required (max 500 characters).");

        document.DeletedAt = clock.GetUtcNow();
        document.DeletedBy = user.UserId;
        document.DeleteReason = reason;
        audit.Record("document.deleted", nameof(AssetDocument), documentId.ToString(), JsonSerializer.Serialize(new { document.AssetId, document.FileName, reason }));
        await db.SaveChangesAsync(ct);
        return Result<DocumentDto>.Ok(DocumentDto.From(document));
    }

    /// <summary>Reads at most <paramref name="max"/> bytes; returns null if the stream holds more. Does not trust any declared length.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream content, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
