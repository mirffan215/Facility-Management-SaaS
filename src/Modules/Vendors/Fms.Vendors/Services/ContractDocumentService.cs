using System.Security.Cryptography;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Storage;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

/// <summary>Files attached to a contract (the signed contract, amendments, insurance certificates). Type is decided from content, every download is audited.</summary>
public sealed class ContractDocumentService(VendorsDbContext db, IFileStore files, IFileScanner scanner, IAuditPort audit, ICurrentUser user, TimeProvider clock)
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const int MaxPerContract = 30;
    private const string StorageKind = "contract-documents";

    public async Task<Result<IReadOnlyList<ContractDocumentDto>>> ListAsync(Guid contractId, CancellationToken ct = default)
    {
        if (!await db.Contracts.AnyAsync(c => c.Id == contractId, ct)) return Result<IReadOnlyList<ContractDocumentDto>>.NotFound();
        var docs = await db.Documents.AsNoTracking().Where(d => d.ContractId == contractId && d.DeletedAt == null).OrderByDescending(d => d.UploadedAt).ToListAsync(ct);
        return Result<IReadOnlyList<ContractDocumentDto>>.Ok(docs.Select(ToDto).ToList());
    }

    public async Task<Result<ContractDocumentDto>> UploadAsync(Guid contractId, ContractDocumentKind kind, string? description, string? originalFileName, Stream content, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct);
        if (contract is null) return Result<ContractDocumentDto>.NotFound();
        var errors = new ValidationErrors();
        if (!Enum.IsDefined(kind)) errors.Add("kind", "Unknown document kind.");
        var note = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (note is { Length: > 500 }) errors.Add("description", "Description is at most 500 characters.");
        if (errors.Any) return errors.ToResult<ContractDocumentDto>();
        if (await db.Documents.CountAsync(d => d.ContractId == contractId && d.DeletedAt == null, ct) >= MaxPerContract) return Result<ContractDocumentDto>.Conflict($"A contract can have at most {MaxPerContract} files.");

        var bytes = await ReadLimitedAsync(content, ct);
        if (bytes is null) return new Result<ContractDocumentDto>(ResultStatus.TooLarge, Message: $"Files can be at most {MaxBytes / (1024 * 1024)} MB.");
        if (bytes.Length == 0) return Result<ContractDocumentDto>.Invalid("file", "The file is empty.");
        var detected = FileSniffer.Detect(bytes);
        if (detected is null) return new Result<ContractDocumentDto>(ResultStatus.UnsupportedType, Message: "This file type is not allowed. Upload a PDF, PNG, JPEG, WebP, Word (.docx), Excel (.xlsx) or plain-text file.");
        if (!(await scanner.ScanAsync(bytes, ct)).IsClean) return Result<ContractDocumentDto>.Invalid("file", "The file was rejected by the security scan.");

        var id = Guid.NewGuid();
        var doc = new ContractDocument
        {
            Id = id, ContractId = contractId, Kind = kind, FileName = DocumentNames.Sanitize(originalFileName, detected.Extension), ContentType = detected.ContentType, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Description = note, UploadedAt = clock.GetUtcNow(), UploadedBy = user.UserId,
        };
        await using (var stream = new MemoryStream(bytes)) doc.StorageKey = await files.PutAsync(StorageKind, id, "content", stream, ct);
        db.Documents.Add(doc);
        audit.Record("contract.document_uploaded", nameof(ContractDocument), id.ToString(), JsonSerializer.Serialize(new { contractId, doc.FileName, doc.ContentType, doc.SizeBytes, doc.Sha256, doc.Kind }));
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

        return Result<ContractDocumentDto>.Ok(ToDto(doc));
    }

    public async Task<Result<DownloadedContractDocument>> DownloadAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (doc is null) return Result<DownloadedContractDocument>.NotFound();
        var stream = await files.GetAsync(doc.StorageKey, ct);
        if (stream is null) return Result<DownloadedContractDocument>.NotFound("The stored file is missing.");
        audit.Record("contract.document_downloaded", nameof(ContractDocument), documentId.ToString(), JsonSerializer.Serialize(new { doc.ContractId, doc.FileName }));
        await db.SaveChangesAsync(ct);
        return Result<DownloadedContractDocument>.Ok(new DownloadedContractDocument(ToDto(doc), stream));
    }

    public async Task<Result<ContractDocumentDto>> DeleteAsync(Guid documentId, DeleteDocumentRequest r, CancellationToken ct = default)
    {
        var doc = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.DeletedAt == null, ct);
        if (doc is null) return Result<ContractDocumentDto>.NotFound();
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? null : r.Reason.Trim();
        if (reason is null || reason.Length > 500) return Result<ContractDocumentDto>.Invalid("reason", "A reason is required (max 500 characters).");
        doc.DeletedAt = clock.GetUtcNow();
        doc.DeletedBy = user.UserId;
        doc.DeleteReason = reason;
        audit.Record("contract.document_deleted", nameof(ContractDocument), documentId.ToString(), JsonSerializer.Serialize(new { doc.ContractId, doc.FileName, reason }));
        await db.SaveChangesAsync(ct);
        return Result<ContractDocumentDto>.Ok(ToDto(doc));
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

    private static ContractDocumentDto ToDto(ContractDocument d) => new(d.Id, d.ContractId, d.Kind, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.Description, d.UploadedAt, d.UploadedBy);
}
