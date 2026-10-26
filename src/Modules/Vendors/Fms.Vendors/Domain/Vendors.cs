using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Vendors.Domain;

/// <summary>Whether work and orders may go to this vendor. On hold and blocked both stop new work; blocked is for a vendor not to be used at all.</summary>
public enum VendorStatus { Active = 1, OnHold = 2, Blocked = 3 }

public sealed class Vendor : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public VendorStatus Status { get; set; } = VendorStatus.Active;
    public string? StatusReason { get; set; }

    /// <summary>VAT registration number as the vendor gave it.</summary>
    public string? TaxNumber { get; set; }

    /// <summary>Commercial registration number.</summary>
    public string? RegistrationNumber { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Country { get; set; }
    public string? PaymentTerms { get; set; }
    public string? Notes { get; set; }

    /// <summary>The vendor's account in the accounting system, filled in by the ERP integration. Never typed by hand.</summary>
    public string? ErpAccount { get; set; }

    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class VendorContact : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public string Name { get; set; } = "";
    public string? Role { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsPrimary { get; set; }
}

public enum ContractType { Amc = 1, Sla = 2, Service = 3, Supply = 4, Other = 5 }

public enum ContractStatus { Upcoming = 1, Active = 2, Expiring = 3, Expired = 4, Terminated = 5 }

public sealed class Contract : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public ContractType Type { get; set; } = ContractType.Amc;
    public DateOnly StartOn { get; set; }
    public DateOnly EndOn { get; set; }
    public decimal? Value { get; set; }
    public string CurrencyCode { get; set; } = "SAR";

    /// <summary>How many days before the end the managers are reminded to renew.</summary>
    public int RenewalNoticeDays { get; set; } = 60;

    public string? Notes { get; set; }
    public DateTimeOffset? TerminatedAt { get; set; }
    public string? TerminationReason { get; set; }

    /// <summary>When the "ending soon" and "ended" reminders went out. Cleared when the end date changes, so an extended contract is reminded again.</summary>
    public DateTimeOffset? ExpiringNoticedAt { get; set; }

    public DateTimeOffset? ExpiredNoticedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum CoverageKind { Site = 1, AssetClass = 2, Asset = 3 }

public sealed class ContractCoverage : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ContractId { get; set; }
    public CoverageKind Kind { get; set; }
    public Guid TargetId { get; set; }
}

/// <summary>What the contract promises for one priority (1 low to 4 urgent). Shown on work orders given to the vendor under it.</summary>
public sealed class ContractSla : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ContractId { get; set; }
    public int Priority { get; set; }
    public int ResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }
}

public enum ContractDocumentKind { Contract = 1, Amendment = 2, Certificate = 3, Insurance = 4, Other = 5 }

public sealed class ContractDocument : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ContractId { get; set; }
    public ContractDocumentKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>The date arithmetic of contracts, free of the database so it can be tested exhaustively.</summary>
public static class ContractRules
{
    public static ContractStatus StatusOn(Contract c, DateOnly today)
    {
        if (c.TerminatedAt is not null) return ContractStatus.Terminated;
        if (today < c.StartOn) return ContractStatus.Upcoming;
        if (today > c.EndOn) return ContractStatus.Expired;
        return c.EndOn.DayNumber - today.DayNumber <= c.RenewalNoticeDays ? ContractStatus.Expiring : ContractStatus.Active;
    }

    /// <summary>Started, not ended and not terminated: the contract can be relied on today (the last day counts).</summary>
    public static bool InForce(Contract c, DateOnly today) => StatusOn(c, today) is ContractStatus.Active or ContractStatus.Expiring;

    public static int DaysLeft(Contract c, DateOnly today) => c.EndOn.DayNumber - today.DayNumber;
}

/// <summary>A person from a vendor who may use the vendor portal, and which vendor they belong to. A person belongs to exactly one vendor. This one row decides everything they can see.</summary>
public sealed class VendorUser : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid VendorId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>False once someone has withdrawn this person's access. The row stays, as the record that they once had it.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? DeactivatedAt { get; set; }
}
