using Bulky.DataAccess.AI.Inventory.Models;

namespace Bulky.DataAccess.AI.Inventory.Interfaces
{
    public interface IInventoryReader
    {
        IReadOnlyList<InventoryStatusResult> GetLowStockProducts();

        InventoryStatusResult? GetProductStock(int productId);

        // Compares SQL quantities against warehouse quantities.
        // WarehouseItems may be empty if the Excel file is unavailable —
        // in that case, discrepancy events are suppressed and only low-stock
        // events fire (graceful degradation, not a hard failure).
        ReconciliationResult GetReconciliation(
            IReadOnlyList<WarehouseStockItem> warehouseItems);
    }
}
