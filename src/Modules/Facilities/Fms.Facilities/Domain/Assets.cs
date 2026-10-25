using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Facilities.Domain;

/// <summary>A category of asset (HVAC, Electrical, Lift...). Classes can be nested; custom fields can be scoped to a class.</summary>
public sealed class AssetClass : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ParentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum AssetStatus { Planned = 1, Active = 2, UnderMaintenance = 3, OutOfService = 4, Retired = 5 }

public enum Criticality { Low = 1, Medium = 2, High = 3, Critical = 4 }

/// <summary>A piece of equipment or infrastructure the tenant maintains.</summary>
public sealed class Asset : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Asset number people use and print on labels. Unique within the tenant, forever (retired assets keep theirs).</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public Guid AssetClassId { get; set; }
    public Guid LocationId { get; set; }

    /// <summary>The assembly this asset is part of (a pump inside a chiller), if any.</summary>
    public Guid? ParentAssetId { get; set; }

    public AssetStatus Status { get; set; } = AssetStatus.Planned;
    public string? StatusReason { get; set; }
    public Criticality Criticality { get; set; } = Criticality.Medium;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public DateOnly? InstallDate { get; set; }
    public DateOnly? WarrantyExpiry { get; set; }

    /// <summary>What a QR/barcode label encodes. Re-issuable if a label is lost; the old value then stops working.</summary>
    public string TagCode { get; set; } = "";

    public string CustomFieldsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public static class AssetRules
{
    public const int MaxDepth = 6;

    /// <summary>Allowed status moves. Retired is final.</summary>
    public static bool CanTransition(AssetStatus from, AssetStatus to) => (from, to) switch
    {
        (AssetStatus.Planned, AssetStatus.Active or AssetStatus.Retired) => true,
        (AssetStatus.Active, AssetStatus.UnderMaintenance or AssetStatus.OutOfService or AssetStatus.Retired) => true,
        (AssetStatus.UnderMaintenance, AssetStatus.Active or AssetStatus.OutOfService or AssetStatus.Retired) => true,
        (AssetStatus.OutOfService, AssetStatus.Active or AssetStatus.UnderMaintenance or AssetStatus.Retired) => true,
        _ => false,
    };

    /// <summary>Taking an asset out of service or retiring it must say why; the reason is part of the maintenance record.</summary>
    public static bool NeedsReason(AssetStatus to) => to is AssetStatus.OutOfService or AssetStatus.Retired;

    public static WarrantyState WarrantyOf(DateOnly? expiry, DateOnly today) => expiry switch
    {
        null => WarrantyState.None,
        { } e when e < today => WarrantyState.Expired,
        { } e when e <= today.AddDays(30) => WarrantyState.ExpiringSoon,
        _ => WarrantyState.Active,
    };
}

public enum WarrantyState { None, Active, ExpiringSoon, Expired }

/// <summary>Human-friendly tag codes for QR/barcode labels: 10 characters from the Crockford base32 alphabet (no I, L, O, U).</summary>
public static class AssetTag
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 10;

    public static string New()
    {
        Span<byte> bytes = stackalloc byte[Length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return string.Create(Length, bytes.ToArray(), static (chars, b) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[b[i] & 31];
        });
    }

    /// <summary>Groups a code for printing: ABCDE-FGHJK.</summary>
    public static string Format(string tag) => tag.Length == Length ? $"{tag[..5]}-{tag[5..]}" : tag;

    /// <summary>Normalises what a person or scanner typed: case, separators and the look-alike characters Crockford maps (O->0, I/L->1).</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var cleaned = new string(input.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant)
            .Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray());
        return cleaned.Length == Length && cleaned.All(c => Alphabet.Contains(c)) ? cleaned : null;
    }
}
