using System.Security.Cryptography;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Storage;
using Fms.Leases.Domain;
using Fms.Leases.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Leases.Services;

/// <summary>The lease itself and its amendments. Type is decided from content, files go through the security scan, every download is audited, removing needs a reason and keeps the record.</summary>
public sealed class LeaseDocumentService(LeasesDbContext db, IFileStore files, IFileScanner scanner, IAuditPort audit, ICurrentUser user, TimeProvider clock)
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int MaxPerLease = 20;
    private const string StorageKind = "lease-documents";

    public async Task<Result<IReadOnlyList<LeaseDocumentDto>>> ListAsync(Guid leaseId, CancellationToken ct = default)
    {
        if (!await db.Leases.AnyAsync(l => l.Id == leaseId, ct)) return Result<IReadOnlyList<LeaseDocumentDto>>.NotFound();
        var rows = await db.Documents.AsNoTracking().Where(d => d.LeaseId == leaseId && d.DeletedAt == null).OrderBy(d => d.UploadedAt).ToListAsync(ct);
        return Result<IReadOnlyList<LeaseDocumentDto>>.Ok(rows.Select(ToDto).ToList());
    }

    public async Task<Result<LeaseDocumentDto>> UploadAsync(Guid leaseId, string? originalFileName, Stream content, CancellationToken ct = default)
    {
        if (!await db.Leases.AnyAsync(l => l.Id == leaseId, ct)) return Result<LeaseDocumentDto>.NotFound();
        if (await db.Documents.CountAsync(d => d.LeaseId == leaseId && d.DeletedAt == null, ct) >= MaxPerLease) return Result<LeaseDocumentDto>.Conflict($"At most {MaxPerLease} files per lease.");

        var bytes = await ReadLimitedAsync(content, ct);
        if (bytes is null) return new Result<LeaseDocumentDto>(ResultStatus.TooLarge, Message: $"Files can be at most {MaxBytes / (1024 * 1024)} MB.");
        if (bytes.Length == 0) return Result<LeaseDocumentDto>.Invalid("file", "The file is empty.");
        var detected = FileSniffer.Detect(bytes);
        if (detected is null) return new Result<LeaseDocumentDto>(ResultStatus.UnsupportedType, Message: "This file type is not allowed. Upload a PDF, PNG, JPEG, WebP, Word (.docx), Excel (.xlsx) or plain-text file.");
        if (!(await scanner.ScanAsync(bytes, ct)).IsClean) return Result<LeaseDocumentDto>.Invalid("file", "The file was rejected by the security scan.");

        var id = Guid.NewGuid();
        var doc = new LeaseDocument
        {
            Id = id, LeaseId = leaseId, FileName = DocumentNames.Sanitize(originalFileName, detected.Extension), ContentType = detected.ContentType, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), UploadedAt = clock.GetUtcNow(), UploadedBy = user.UserId,
        };
        await using (var stream = new MemoryStream(bytes)) doc.StorageKey = await files.PutAsync(StorageKind, id, "content", stream, ct);
        db.Documents.Add(doc);
        audit.Record("lease.document_uploaded", nameof(LeaseDocument), id.ToString(), JsonSerializer.Serialize(new { leaseId, doc.FileName, doc.ContentType, doc.SizeBytes, doc.Sha256 }));
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

        return Result<LeaseDocumentDto>.Ok(ToDto(doc));
    }

    public async Task<Result<DownloadedLeaseDocument>> DownloadAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (doc is null) return Result<DownloadedLeaseDocument>.NotFound();
        var stream = await files.GetAsync(doc.StorageKey, ct);
        if (stream is null) return Result<DownloadedLeaseDocument>.NotFound("The stored file is missing.");
        audit.Record("lease.document_downloaded", nameof(LeaseDocument), documentId.ToString(), JsonSerializer.Serialize(new { doc.LeaseId, doc.FileName }));
        await db.SaveChangesAsync(ct);
        return Result<DownloadedLeaseDocument>.Ok(new DownloadedLeaseDocument(ToDto(doc), stream));
    }

    public async Task<Result<LeaseDocumentDto>> DeleteAsync(Guid documentId, DeleteRequest r, CancellationToken ct = default)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (doc is null) return Result<LeaseDocumentDto>.NotFound();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<LeaseDocumentDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        doc.DeletedAt = clock.GetUtcNow();
        doc.DeletedBy = user.UserId;
        doc.DeleteReason = reason;
        audit.Record("lease.document_deleted", nameof(LeaseDocument), documentId.ToString(), JsonSerializer.Serialize(new { doc.LeaseId, doc.FileName, reason }));
        await db.SaveChangesAsync(ct);
        return Result<LeaseDocumentDto>.Ok(ToDto(doc));
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

    private static LeaseDocumentDto ToDto(LeaseDocument d) => new(d.Id, d.LeaseId, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.UploadedAt);
}
