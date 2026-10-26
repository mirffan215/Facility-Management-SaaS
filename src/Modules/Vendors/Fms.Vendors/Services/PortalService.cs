using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

public sealed record PortalMeDto(Guid VendorId, string VendorName, string? VendorNameAr, VendorStatus Status);

/// <summary>A contract as its vendor sees it: the terms, not the buyer's notes.</summary>
public sealed record PortalContractDto(
    Guid Id, string Number, string Title, ContractType Type, DateOnly StartOn, DateOnly EndOn, ContractStatus Status, int DaysLeft, decimal? Value, string CurrencyCode,
    IReadOnlyList<CoverageDto> Coverage, IReadOnlyList<ContractSlaDto> Sla, int Documents);

/// <summary>What a vendor user sees of their own vendor's side: who they are and their contracts and contract files. Always filtered by <see cref="IVendorScope"/>.</summary>
public sealed class PortalService(VendorsDbContext db, ContractService contracts, ContractDocumentService documents, IVendorScope scope)
{
    public async Task<Result<PortalMeDto>> MeAsync(CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId) return Result<PortalMeDto>.Forbidden("You do not have access to the vendor portal.");
        var v = await db.Vendors.AsNoTracking().FirstAsync(x => x.Id == vendorId, ct);
        return Result<PortalMeDto>.Ok(new PortalMeDto(v.Id, v.Name, v.NameAr, v.Status));
    }

    public async Task<Result<IReadOnlyList<PortalContractDto>>> ContractsAsync(CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId) return Result<IReadOnlyList<PortalContractDto>>.Forbidden("You do not have access to the vendor portal.");
        var page = await contracts.ListAsync(vendorId, null, null, 1, 100, ct);
        return Result<IReadOnlyList<PortalContractDto>>.Ok(page.Items.Select(ToPortal).ToList());
    }

    public async Task<Result<IReadOnlyList<ContractDocumentDto>>> DocumentsAsync(Guid contractId, CancellationToken ct = default)
    {
        if (await OwnContractAsync(contractId, ct) is false) return Result<IReadOnlyList<ContractDocumentDto>>.NotFound();
        return await documents.ListAsync(contractId, ct);
    }

    public async Task<Result<DownloadedContractDocument>> DownloadAsync(Guid documentId, CancellationToken ct = default)
    {
        var contractId = await db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.DeletedAt == null).Select(d => (Guid?)d.ContractId).FirstOrDefaultAsync(ct);
        if (contractId is null || await OwnContractAsync(contractId.Value, ct) is false) return Result<DownloadedContractDocument>.NotFound();
        return await documents.DownloadAsync(documentId, ct);
    }

    /// <summary>True when the contract is the signed-in vendor's. A contract of another vendor looks exactly like one that does not exist.</summary>
    private async Task<bool> OwnContractAsync(Guid contractId, CancellationToken ct) =>
        await scope.CurrentVendorIdAsync(ct) is { } vendorId && await db.Contracts.AsNoTracking().AnyAsync(c => c.Id == contractId && c.VendorId == vendorId, ct);

    private static PortalContractDto ToPortal(ContractDto c) => new(c.Id, c.Number, c.Title, c.Type, c.StartOn, c.EndOn, c.Status, c.DaysLeft, c.Value, c.CurrencyCode, c.Coverage, c.Sla, c.Documents);
}
