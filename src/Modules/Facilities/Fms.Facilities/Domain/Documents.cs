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
