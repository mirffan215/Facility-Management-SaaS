using System.Security.Cryptography;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Storage;
using Fms.Compliance.Domain;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Compliance.Services;

/// <summary>Certificates and reports attached to a completion. Type is decided from content, every download is audited, deleting needs a reason and keeps the record.</summary>
public sealed class CompletionDocumentService(ComplianceDbContext db, IFileStore files, IFileScanner scanner, IAuditPort audit, ICurrentUser user, TimeProvider clock)
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int MaxPerCompletion = 10;
    private const string StorageKind = "compliance-documents";

    public async Task<Result<CompletionDocumentDto>> UploadAsync(Guid completionId, string? originalFileName, Stream content, CancellationToken ct = default)
    {
        if (!await db.Completions.AnyAsync(c => c.Id == completionId, ct)) return Result<CompletionDocumentDto>.NotFound();
        if (await db.Documents.CountAsync(d => d.CompletionId == completionId && d.DeletedAt == null, ct) >= MaxPerCompletion) return Result<CompletionDocumentDto>.Conflict($"At most {MaxPerCompletion} files per record.");

        var bytes = await ReadLimitedAsync(content, ct);
        if (bytes is null) return new Result<CompletionDocumentDto>(ResultStatus.TooLarge, Message: $"Files can be at most {MaxBytes / (1024 * 1024)} MB.");
        if (bytes.Length == 0) return Result<CompletionDocumentDto>.Invalid("file", "The file is empty.");
        var detected = FileSniffer.Detect(bytes);
        if (detected is null) return new Result<CompletionDocumentDto>(ResultStatus.UnsupportedType, Message: "This file type is not allowed. Upload a PDF, PNG, JPEG, WebP, Word (.docx), Excel (.xlsx) or plain-text file.");
        if (!(await scanner.ScanAsync(bytes, ct)).IsClean) return Result<CompletionDocumentDto>.Invalid("file", "The file was rejected by the security scan.");

        var id = Guid.NewGuid();
        var doc = new CompletionDocument
        {
            Id = id, CompletionId = completionId, FileName = DocumentNames.Sanitize(originalFileName, detected.Extension), ContentType = detected.ContentType, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), UploadedAt = clock.GetUtcNow(), UploadedBy = user.UserId,
        };
        await using (var stream = new MemoryStream(bytes)) doc.StorageKey = await files.PutAsync(StorageKind, id, "content", stream, ct);
        db.Documents.Add(doc);
        audit.Record("compliance.document_uploaded", nameof(CompletionDocument), id.ToString(), JsonSerializer.Serialize(new { completionId, doc.FileName, doc.ContentType, doc.SizeBytes, doc.Sha256 }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await files.DeleteAsync(doc.StorageKey, CancellationToken.None);
            throw;
        }

        return Result<CompletionDocumentDto>.Ok(ToDto(doc));
    }

    public async Task<Result<DownloadedDocument>> DownloadAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (doc is null) return Result<DownloadedDocument>.NotFound();
        var stream = await files.GetAsync(doc.StorageKey, ct);
        if (stream is null) return Result<DownloadedDocument>.NotFound("The stored file is missing.");
        audit.Record("compliance.document_downloaded", nameof(CompletionDocument), documentId.ToString(), JsonSerializer.Serialize(new { doc.CompletionId, doc.FileName }));
        await db.SaveChangesAsync(ct);
        return Result<DownloadedDocument>.Ok(new DownloadedDocument(ToDto(doc), stream));
    }

    public async Task<Result<CompletionDocumentDto>> DeleteAsync(Guid documentId, DeleteDocumentRequest r, CancellationToken ct = default)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (doc is null) return Result<CompletionDocumentDto>.NotFound();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<CompletionDocumentDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        doc.DeletedAt = clock.GetUtcNow();
        doc.DeletedBy = user.UserId;
        doc.DeleteReason = reason;
        audit.Record("compliance.document_deleted", nameof(CompletionDocument), documentId.ToString(), JsonSerializer.Serialize(new { doc.CompletionId, doc.FileName, reason }));
        await db.SaveChangesAsync(ct);
        return Result<CompletionDocumentDto>.Ok(ToDto(doc));
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static CompletionDocumentDto ToDto(CompletionDocument d) => new(d.Id, d.CompletionId, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.UploadedAt);
}
