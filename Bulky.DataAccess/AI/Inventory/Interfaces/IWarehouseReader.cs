using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Bulky.DataAccess.AI.Inventory.Interfaces
{
    public record WarehouseStockItem(
        int ProductId, 
        string ProductName, 
        int WarehouseQuantity
        );

    public interface IWarehouseReader
    {
        // Downloads warehouse_stock.xlsx from Azure Blob Storage and parses every row.
        // Returns an empty list if the blob is not found (404) — the caller then
        // degrades gracefully to low-stock-only detection. The download is async,
        // so the method is async.
        Task<IReadOnlyList<WarehouseStockItem>> ReadAllAsync(CancellationToken cancellationToken = default);
    }
}
