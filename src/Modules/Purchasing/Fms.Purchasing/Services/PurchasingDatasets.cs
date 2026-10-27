using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

public static class PurchasingDatasets
{
    public static IEnumerable<IDatasetProvider> All(PurchasingDbContext db)
    {
        yield return new Dataset<Domain.PurchaseOrder>("PurchaseOrders", Permissions.PurchasingRead,
            [new("Id", "Edm.Guid"), new("Number", "Edm.Int32"), new("VendorId", "Edm.Guid"), new("VendorName", "Edm.String"), new("Status", "Edm.String"), new("CurrencyCode", "Edm.String"), new("Total", "Edm.Decimal"),
             new("ExpectedOn", "Edm.String"), new("IssuedAt", "Edm.DateTimeOffset"), new("AcknowledgedAt", "Edm.DateTimeOffset")],
            () => db.Orders.AsNoTracking(), o => o.Id,
            o => DatasetRow.Of(("Id", o.Id), ("Number", o.Number), ("VendorId", o.VendorId), ("VendorName", o.VendorName), ("Status", o.Status.ToString()), ("CurrencyCode", o.CurrencyCode), ("Total", o.Total),
                ("ExpectedOn", o.ExpectedOn?.ToString("yyyy-MM-dd")), ("IssuedAt", o.IssuedAt), ("AcknowledgedAt", o.AcknowledgedAt)),
            "IssuedAt", o => o.IssuedAt);
    }
}
