using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Bulky.DataAccess.AI.Inventory.Models
{
    public enum ReconciliationEventType
    {
        LowStock,           // SQL below threshold, no significant discrepancy
        UrgentDiscrepancy   // >40% SQL vs Excel divergence

    }

    public record ReconciliationItem(
        int ProductId,
        string ProductName,
        int SqlQuantity,
        int WarehouseQuantity,
        int DiscrepancyPercentage,
        ReconciliationEventType EventType
    );

    public record ReconciliationResult(
        IReadOnlyList<ReconciliationItem> Items,
        int LowStockCount,
        int UrgentDiscrepancyCount
    );
}
