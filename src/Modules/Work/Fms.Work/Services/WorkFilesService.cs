using System.Security.Cryptography;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Storage;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fms.Work.Services;

/// <summary>Photos and documents on a work order, and the signatures that sign it off. File types come from content, never from the client.</summary>
public sealed class WorkFilesService(
    WorkDbContext db, IFileStore files, IFileScanner scanner, IAuditPort audit, ICurrentUser user, ICurrentPermissions permissions, IUserDirectory users,
    IOptions<WorkOptions> options, TimeProvider clock)
{
    private const string AttachmentKind = "work-attachments";
    private const string SignatureKind = "work-signatures";
    private Actor? _actor;

    private async Task<Actor> ActorAsync(CancellationToken ct) => _actor ??= new Actor(user.UserId, await permissions.GetAsync(ct));

    private async Task<WorkOrder?> LoadAsync(Guid id, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var query = db.WorkOrders.AsNoTracking().AsQueryable();
        if (!actor.Has(Permissions.WorkOrdersRead))
        {
            var me = actor.UserId;
            query = query.Where(w => w.RequestedBy == me);
        }

        return await query.FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    // ---------------- attachments ----------------

    public async Task<Result<IReadOnlyList<AttachmentDto>>> AttachmentsAsync(Guid workOrderId, CancellationToken ct = default)
    {
        if (await LoadAsync(workOrderId, ct) is null) return Result<IReadOnlyList<AttachmentDto>>.NotFound();
        var rows = await db.Attachments.AsNoTracking().Where(a => a.WorkOrderId == workOrderId && a.DeletedAt == null).OrderBy(a => a.UploadedAt).ToListAsync(ct);
        var names = await users.GetAsync(rows.Select(r => r.UploadedBy), ct);
        return Result<IReadOnlyList<AttachmentDto>>.Ok(rows.Select(r => ToDto(r, names)).ToList());
    }

    public async Task<Result<AttachmentDto>> UploadAsync(Guid workOrderId, AttachmentKind kind, string? description, string? originalFileName, Stream content, CancellationToken ct = default)
    {
        var o = options.Value;
        var wo = await LoadAsync(workOrderId, ct);
        if (wo is null) return Result<AttachmentDto>.NotFound();
        var actor = await ActorAsync(ct);
        if (!EvidenceRules.MayAddFiles(wo, actor)) return Result<AttachmentDto>.Forbidden("You cannot add files to this work order.");
        if (!EvidenceRules.FilesOpen(wo.Status)) return Result<AttachmentDto>.Conflict($"Files cannot be added to work that is {wo.Status}.");

        var note = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var errors = new ValidationErrors();
        if (!Enum.IsDefined(kind)) errors.Add("kind", "Unknown kind.");
        if (note is { Length: > 500 }) errors.Add("description", "Description is at most 500 characters.");
        if (errors.Any) return errors.ToResult<AttachmentDto>();
        if (await db.Attachments.CountAsync(a => a.WorkOrderId == workOrderId && a.DeletedAt == null, ct) >= o.MaxAttachmentsPerOrder)
            return Result<AttachmentDto>.Conflict($"A work order can have at most {o.MaxAttachmentsPerOrder} files.");

        var bytes = await ReadLimitedAsync(content, o.MaxAttachmentBytes, ct);
        if (bytes is null) return new Result<AttachmentDto>(ResultStatus.TooLarge, Message: $"Files can be at most {o.MaxAttachmentBytes / (1024 * 1024)} MB.");
        if (bytes.Length == 0) return Result<AttachmentDto>.Invalid("file", "The file is empty.");

        var detected = FileSniffer.Detect(bytes);
        if (detected is null)
            return new Result<AttachmentDto>(ResultStatus.UnsupportedType, Message: "This file type is not allowed. Upload a photo (JPEG, PNG, WebP), PDF, Word, Excel or plain-text file.");
        if (!(await scanner.ScanAsync(bytes, ct)).IsClean) return Result<AttachmentDto>.Invalid("file", "The file was rejected by the security scan.");

        var id = Guid.NewGuid();
        var row = new WorkOrderAttachment
        {
            Id = id, WorkOrderId = workOrderId, Kind = kind, FileName = DocumentNames.Sanitize(originalFileName, detected.Extension), ContentType = detected.ContentType,
            SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Description = note, UploadedAt = clock.GetUtcNow(), UploadedBy = actor.UserId!.Value,
        };
        await using (var stream = new MemoryStream(bytes)) row.StorageKey = await files.PutAsync(AttachmentKind, id, "content", stream, ct);

        db.Attachments.Add(row);
        audit.Record("workattachment.uploaded", nameof(WorkOrderAttachment), id.ToString(), JsonSerializer.Serialize(new { workOrderId, row.FileName, row.ContentType, row.SizeBytes, row.Sha256, row.Kind }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await files.DeleteAsync(row.StorageKey, CancellationToken.None);
            throw;
        }

        return Result<AttachmentDto>.Ok(ToDto(row, await users.GetAsync([row.UploadedBy], ct)));
    }

    public async Task<Result<DownloadedFile>> DownloadAttachmentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var row = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.DeletedAt == null, ct);
        if (row is null || await LoadAsync(row.WorkOrderId, ct) is null) return Result<DownloadedFile>.NotFound();

        var stream = await files.GetAsync(row.StorageKey, ct);
        if (stream is null) return Result<DownloadedFile>.NotFound("The stored file is missing.");
        audit.Record("workattachment.downloaded", nameof(WorkOrderAttachment), attachmentId.ToString(), JsonSerializer.Serialize(new { row.WorkOrderId, row.FileName }));
        await db.SaveChangesAsync(ct);
        return Result<DownloadedFile>.Ok(new DownloadedFile(row.FileName, row.ContentType, stream));
    }

    public async Task<Result<AttachmentDto>> DeleteAttachmentAsync(Guid attachmentId, string? reason, CancellationToken ct = default)
    {
        var row = await db.Attachments.FirstOrDefaultAsync(a => a.Id == attachmentId && a.DeletedAt == null, ct);
        if (row is null) return Result<AttachmentDto>.NotFound();
        var wo = await LoadAsync(row.WorkOrderId, ct);
        if (wo is null) return Result<AttachmentDto>.NotFound();

        var actor = await ActorAsync(ct);
        if (!(actor.Has(Permissions.WorkOrdersManage) || row.UploadedBy == actor.UserId)) return Result<AttachmentDto>.Forbidden("Only the person who added a file (or a manager) can remove it.");
        if (!EvidenceRules.FilesOpen(wo.Status)) return Result<AttachmentDto>.Conflict($"Files cannot be removed from work that is {wo.Status}.");
        var why = reason?.Trim() ?? "";
        if (why.Length is 0 or > 500) return Result<AttachmentDto>.Invalid("reason", "A reason is required (max 500 characters).");

        row.DeletedAt = clock.GetUtcNow();
        row.DeletedBy = actor.UserId;
        row.DeleteReason = why;
        audit.Record("workattachment.deleted", nameof(WorkOrderAttachment), attachmentId.ToString(), JsonSerializer.Serialize(new { row.WorkOrderId, row.FileName, reason = why }));
        await db.SaveChangesAsync(ct);
        return Result<AttachmentDto>.Ok(ToDto(row, await users.GetAsync([row.UploadedBy], ct)));
    }

    // ---------------- signatures ----------------

    public async Task<Result<IReadOnlyList<SignatureDto>>> SignaturesAsync(Guid workOrderId, CancellationToken ct = default)
    {
        if (await LoadAsync(workOrderId, ct) is null) return Result<IReadOnlyList<SignatureDto>>.NotFound();
        return Result<IReadOnlyList<SignatureDto>>.Ok(await SignatureDtosAsync(workOrderId, ct));
    }

    public async Task<Result<IReadOnlyList<SignatureDto>>> AddSignatureAsync(Guid workOrderId, AddSignatureRequest r, CancellationToken ct = default)
    {
        var wo = await LoadAsync(workOrderId, ct);
        if (wo is null) return Result<IReadOnlyList<SignatureDto>>.NotFound();
        var actor = await ActorAsync(ct);
        if (!EvidenceRules.DoesTheWork(wo, actor)) return Result<IReadOnlyList<SignatureDto>>.Forbidden("Only the person doing the work (or a manager) records signatures.");
        if (!EvidenceRules.SignatureOpen(wo.Status)) return Result<IReadOnlyList<SignatureDto>>.Conflict($"Signatures cannot be taken on work that is {wo.Status}.");

        var name = r.SignerName?.Trim() ?? "";
        var errors = new ValidationErrors();
        if (r.Role is not { } role || !Enum.IsDefined(role)) errors.Add("role", "Say who is signing (technician or customer).");
        if (name.Length is 0 or > 200) errors.Add("signerName", "Print the signer's name (max 200 characters).");
        byte[]? image = null;
        if (string.IsNullOrWhiteSpace(r.ImageBase64)) errors.Add("image", "A signature is required.");
        else if (r.ImageBase64.Length > options.Value.MaxSignatureBytes * 2) errors.Add("image", "The signature image is too large.");
        else
        {
            var payload = r.ImageBase64.Contains(',') ? r.ImageBase64[(r.ImageBase64.IndexOf(',') + 1)..] : r.ImageBase64; // accept a data: URL as a canvas produces it
            try { image = Convert.FromBase64String(payload); }
            catch (FormatException) { errors.Add("image", "The signature image is not valid."); }
            if (image is not null && (image.Length > options.Value.MaxSignatureBytes || FileSniffer.Detect(image)?.ContentType != "image/png")) errors.Add("image", "The signature must be a PNG image of reasonable size.");
        }

        if (errors.Any) return errors.ToResult<IReadOnlyList<SignatureDto>>();
        if (await db.Signatures.CountAsync(s => s.WorkOrderId == workOrderId, ct) >= 10) return Result<IReadOnlyList<SignatureDto>>.Conflict("A work order can have at most 10 signatures.");

        var id = Guid.NewGuid();
        var row = new WorkOrderSignature
        {
            Id = id, WorkOrderId = workOrderId, Role = r.Role!.Value, SignerName = name, Sha256 = Convert.ToHexString(SHA256.HashData(image!)).ToLowerInvariant(),
            SignedAt = clock.GetUtcNow(), RecordedBy = actor.UserId!.Value,
        };
        await using (var stream = new MemoryStream(image!)) row.StorageKey = await files.PutAsync(SignatureKind, id, "signature", stream, ct);

        db.Signatures.Add(row);
        audit.Record("workorder.signed", nameof(WorkOrder), workOrderId.ToString(), JsonSerializer.Serialize(new { signatureId = id, row.Role, row.SignerName, row.Sha256 }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            db.ChangeTracker.Clear();
            await files.DeleteAsync(row.StorageKey, CancellationToken.None);
            throw;
        }

        return Result<IReadOnlyList<SignatureDto>>.Ok(await SignatureDtosAsync(workOrderId, ct));
    }

    public async Task<Result<DownloadedFile>> SignatureImageAsync(Guid signatureId, CancellationToken ct = default)
    {
        var row = await db.Signatures.AsNoTracking().FirstOrDefaultAsync(s => s.Id == signatureId, ct);
        if (row is null || await LoadAsync(row.WorkOrderId, ct) is null) return Result<DownloadedFile>.NotFound();
        var stream = await files.GetAsync(row.StorageKey, ct);
        return stream is null ? Result<DownloadedFile>.NotFound("The stored file is missing.") : Result<DownloadedFile>.Ok(new DownloadedFile("signature.png", "image/png", stream));
    }

    private async Task<IReadOnlyList<SignatureDto>> SignatureDtosAsync(Guid workOrderId, CancellationToken ct)
    {
        var rows = await db.Signatures.AsNoTracking().Where(s => s.WorkOrderId == workOrderId).OrderBy(s => s.SignedAt).ToListAsync(ct);
        var names = await users.GetAsync(rows.Select(r => r.RecordedBy), ct);
        return rows.Select(r => new SignatureDto(r.Id, r.Role, r.SignerName, r.SignedAt, names.TryGetValue(r.RecordedBy, out var p) ? p.DisplayName ?? p.Email : null)).ToList();
    }

    private static AttachmentDto ToDto(WorkOrderAttachment a, IReadOnlyDictionary<Guid, DirectoryUser> names) =>
        new(a.Id, a.Kind, a.FileName, a.ContentType, a.SizeBytes, a.Description, a.UploadedAt, names.TryGetValue(a.UploadedBy, out var p) ? p.DisplayName ?? p.Email : null, a.UploadedBy);

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
