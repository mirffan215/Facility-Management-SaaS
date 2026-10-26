using Fms.Vendors.Domain;

namespace Fms.Vendors.Services;

public sealed record VendorDto(
    Guid Id, string Code, string Name, string? NameAr, VendorStatus Status, string? StatusReason, string? TaxNumber, string? RegistrationNumber, string? Email, string? Phone,
    string? Address, string? Country, string? PaymentTerms, string? Notes, string? ErpAccount, bool IsArchived, int ActiveContracts, string RowVersion);

public sealed record SaveVendorRequest(
    string? Code, string? Name, string? NameAr, string? TaxNumber, string? RegistrationNumber, string? Email, string? Phone, string? Address, string? Country, string? PaymentTerms,
    string? Notes, string? RowVersion);

public sealed record SetVendorStatusRequest(VendorStatus? Status, string? Reason, string? RowVersion);

public sealed record VendorContactDto(Guid Id, string Name, string? Role, string? Email, string? Phone, bool IsPrimary);

public sealed record ContactInput(string? Name, string? Role, string? Email, string? Phone, bool IsPrimary);

public sealed record SaveContactsRequest(IReadOnlyList<ContactInput>? Contacts);

public sealed record CoverageDto(CoverageKind Kind, Guid TargetId, string? Name);

public sealed record CoverageInput(CoverageKind? Kind, Guid? TargetId);

public sealed record SaveCoverageRequest(IReadOnlyList<CoverageInput>? Coverage);

public sealed record ContractSlaDto(int Priority, int ResponseMinutes, int ResolutionMinutes);

public sealed record SlaInput(int? Priority, int? ResponseMinutes, int? ResolutionMinutes);

public sealed record SaveSlaRequest(IReadOnlyList<SlaInput>? Targets);

public sealed record ContractDto(
    Guid Id, Guid VendorId, string? VendorName, string Number, string Title, ContractType Type, DateOnly StartOn, DateOnly EndOn, decimal? Value, string CurrencyCode, int RenewalNoticeDays,
    string? Notes, ContractStatus Status, int DaysLeft, DateTimeOffset? TerminatedAt, string? TerminationReason, IReadOnlyList<CoverageDto> Coverage, IReadOnlyList<ContractSlaDto> Sla,
    int Documents, string RowVersion);

public sealed record SaveContractRequest(
    Guid? VendorId, string? Number, string? Title, ContractType? Type, DateOnly? StartOn, DateOnly? EndOn, decimal? Value, string? CurrencyCode, int? RenewalNoticeDays, string? Notes,
    string? RowVersion);

public sealed record TerminateContractRequest(string? Reason, string? RowVersion);

public sealed record ContractDocumentDto(
    Guid Id, Guid ContractId, ContractDocumentKind Kind, string FileName, string ContentType, long SizeBytes, string Sha256, string? Description, DateTimeOffset UploadedAt, Guid? UploadedBy);

public sealed record DownloadedContractDocument(ContractDocumentDto Document, Stream Content);

public sealed record DeleteDocumentRequest(string? Reason);

public sealed record RowVersionRequest(string? RowVersion);

public enum ContractAlertKind { Expiring = 1, Expired = 2 }

public sealed record ContractAlertMessage(Guid ContractId, ContractAlertKind Kind);

public static class VendorMessages
{
    public const string ContractAlert = "contract.alert";
}
