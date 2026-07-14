using Bulky.DataAccess.AI.Inventory.Interfaces;
using Bulky.DataAccess.AI.Inventory.Models;
using Bulky.DataAccess.Repository.IRepository;
using DocumentFormat.OpenXml.Office.CustomUI;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace Bulky.DataAccess.AI.Inventory.Services
{
    public class InventoryReader : IInventoryReader
    {
        public const int LowStockThreshold = 5;

        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<InventoryReader> _logger;

        public InventoryReader(IUnitOfWork unitOfWork, ILogger<InventoryReader> logger) {
            _unitOfWork = unitOfWork;
            _logger = logger;
        }

        [Description("Returns every product at or below the low-stock threshold, " +
                "each with its current stock quantity.")]
        public IReadOnlyList<InventoryStatusResult> GetLowStockProducts() {
            var lowStockProducts = _unitOfWork.Product
                .GetAll(p => p.StockQuantity <= LowStockThreshold)
                .Select(p => new InventoryStatusResult(
                    p.Id,
                    p.Title,
                    p.StockQuantity,
                    IsLowStock: true))
                .ToList();
            _logger.LogInformation("[Inventory] Low-stock scan — {Count} product(s) at or below {Threshold}",
            lowStockProducts.Count, LowStockThreshold);
            return lowStockProducts;
        }

        [Description("Returns the current stock quantity for a single product " +
                "by its numeric ID.")]
        public InventoryStatusResult? GetProductStock(int productId) {
            var product = _unitOfWork.Product.Get(p => p.Id == productId);
            if(product == null) {
                _logger.LogWarning("[Inventory] Product ID {ProductId} not found when checking stock.", productId);
                return null;
            }
            return new InventoryStatusResult(
                product.Id,
                product.Title,
                product.StockQuantity,
                IsLowStock: product.StockQuantity <= LowStockThreshold);

        }

        public ReconciliationResult GetReconciliationResult(
            IReadOnlyList<WarehouseStockItem> warehouseItems) {
            
            var products = _unitOfWork.Product.GetAll().ToList();
            var warehouseDict = warehouseItems.ToDictionary(w => w.ProductId);

            var reconciliationItems = new List<ReconciliationItem>();
            int lowStockCount = 0;
            int urgentDiscrepancyCount = 0;

            foreach(var product in products) {

                bool isLowStock = product.StockQuantity <= LowStockThreshold;

                if((!warehouseDict.TryGetValue(product.Id, out var warehouseItem)) {

                    if(isLowStock) {
                        reconciliationItems.Add(new ReconciliationItem(
                            product.Id,
                            product.Title,
                            product.StockQuantity,
                            WarehouseQuantity: 0,
                            DiscrepancyPercentage: 100,
                            EventType: ReconciliationEventType.LowStock));
                        lowStockCount++;
                    }
                    continue;
                }

                //Discrepancy percent calculation |SQL - Warehouse| / max(SQL, Warehouse, 1) * 100
                int maxQuantity = Math.Max(Math.Max(warehouseItem.WarehouseQuantity, product.StockQuantity), 1);
                int discrepancyPercent = (int)(Math.Abs(product.StockQuantity - warehouseItem.WarehouseQuantity) / (double)maxQuantity * 100);

                if(discrepancyPercent > 40) {

                    reconciliationItems.Add(new ReconciliationItem(
                        product.Id,
                        product.Title,
                        product.StockQuantity,
                        warehouseItem.WarehouseQuantity,
                        discrepancyPercent,
                        ReconciliationEventType.UrgentDiscrepancy));
                    urgentDiscrepancyCount++;

                } else if(isLowStock) {
                    reconciliationItems.Add(new ReconciliationItem(
                        product.Id,
                        product.Title,
                        product.StockQuantity,
                        warehouseItem.WarehouseQuantity,
                        discrepancyPercent,
                        ReconciliationEventType.LowStock));
                    lowStockCount++;
                }

            }

            _logger.LogInformation(
                "[Inventory] Reconciliation — {Low} low-stock, {Urgent} urgent discrepancies",
                lowStockCount, urgentDiscrepancyCount);

            return new ReconciliationResult( 
                reconciliationItems, 
                lowStockCount, 
                urgentDiscrepancyCount);

        }
    }
}
