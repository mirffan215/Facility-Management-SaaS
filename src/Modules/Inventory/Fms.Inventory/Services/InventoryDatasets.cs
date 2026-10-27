using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

public static class InventoryDatasets
{
    public static IEnumerable<IDatasetProvider> All(InventoryDbContext db)
    {
        yield return new Dataset<Domain.StockMovement>("StockMovements", Permissions.InventoryRead,
            [new("Id", "Edm.Guid"), new("StoreId", "Edm.Guid"), new("ItemId", "Edm.Guid"), new("Type", "Edm.String"), new("Quantity", "Edm.Decimal"), new("UnitCost", "Edm.Decimal"), new("Value", "Edm.Decimal"),
             new("BalanceAfter", "Edm.Decimal"), new("WorkOrderId", "Edm.Guid"), new("At", "Edm.DateTimeOffset")],
            () => db.Movements.AsNoTracking(), m => m.Id,
            m => DatasetRow.Of(("Id", m.Id), ("StoreId", m.StoreId), ("ItemId", m.ItemId), ("Type", m.Type.ToString()), ("Quantity", m.Quantity), ("UnitCost", m.UnitCost), ("Value", Math.Round(m.Quantity * m.UnitCost, 2)),
                ("BalanceAfter", m.BalanceAfter), ("WorkOrderId", m.WorkOrderId), ("At", m.At)),
            "At", m => m.At);
    }
}
