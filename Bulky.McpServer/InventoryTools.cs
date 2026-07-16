using Azure;
using Azure.Storage.Blobs;
using Bulky.DataAccess.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Bulky.McpServer
{
    [McpServerToolType]
    public static class InventoryTools
    {
        private const int LowStockTHreshold = 5;

        [McpServerTool(Name = "get_low_inventory_products")]
        [Description("List all products at or below the low-stock threshold," +
            "each with its current quantity.")]
        public static async Task<string> GetLowInventoryProducts(
            IDbContextFactory<ApplicationDbContext> dbFactory,
            CancellationToken ct) {

            await using var db=await dbFactory.CreateDbContextAsync(ct);

            var low = await db.Products
                .Where(p=>p.StockQuantity <= LowStockTHreshold)
                .Select(p=>new {p.Id, p.Title, p.StockQuantity})
                .ToListAsync(ct);

            return JsonSerializer.Serialize(new {
                threshold = LowStockTHreshold,
                count =low.Count,
                products = low 
            });
        }

        [McpServerTool(Name = "get_product_stock")]
        [Description("Get the current stock quantity for a single product by its " +
                 "numeric ID.")]
        public static async Task<string> GetProductStock(
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            [Description("The numeric product ID")] int productId,
            CancellationToken ct) {

            await using var db = await dbContextFactory.CreateDbContextAsync(ct);

            var p = await db.Products
                .Where(p => p.Id == productId)
                .Select(x => new { x.Id, x.Title, x.StockQuantity })
                .FirstOrDefaultAsync(ct);

            return p is null ?
                JsonSerializer.Serialize(new { error = $"Product {productId} not found." })
                : JsonSerializer.Serialize(p);
        }

        [McpServerTool(Name ="get_stock_discrepancies")]
        [Description("Lists products where SQL stock quantity and warehouse quantity " +
                "diverge by more than 40%. Returns count and product details.")]
        public static async Task<string> GetStockDiscrepancies(
                                    IDbContextFactory<ApplicationDbContext> dbContext,
                                    BlobServiceClient blobServiceClient,
                                    IConfiguration configuration,
                                    CancellationToken ct) 
        {
            await using var db = await dbContext.CreateDbContextAsync(ct);

            var products = await db.Products
                .Select(p => new { p.Id, p.Title, p.StockQuantity })
                .ToListAsync(ct);

            // Read the SAME warehouse blob the orchestrator reads (single source of truth).
            var containerName = configuration["Inventory:WarehouseContainer"] ?? "inventory-data";
            var blobName = configuration["Inventory:WarehouseBlobName"] ?? "warehouse_stock.xlsx";

            var blob = blobServiceClient
                            .GetBlobContainerClient(containerName)
                            .GetBlobClient(blobName);

            Dictionary<int, int> warehouseMap = [];

            try {
                using var stream = new MemoryStream();
                await blob.DownloadToAsync(stream, cancellationToken: ct);
                stream.Position = 0;

                using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
                var sheet = workbook.Worksheets.First();
                foreach(var row in sheet.RowsUsed().Skip(1)) {
                    
                    var idCell = row.Cell(1).GetValue<string>();
                    var qtyCell = row.Cell(3).GetValue<string>();

                    if (int.TryParse(idCell, out int id) &&
                        int.TryParse(qtyCell, out int qty)) 
                    {
                        warehouseMap[id] = qty;
                    }
                }
            } 
            catch(RequestFailedException ex) when(ex.Status == 404) {
                // No warehouse blob — return an empty discrepancy set rather than failing.
            }

            var discrepancies = new List<object>();

            foreach(var p in products)     
            {
                if(!warehouseMap.TryGetValue(p.Id, out int warehouseQty))
                    continue;
                int maxQty = Math.Max(Math.Max(p.StockQuantity, warehouseQty), 1);
                int percentDiff = Math.Abs(p.StockQuantity - warehouseQty) * 100 / maxQty;

                if(percentDiff > 40) {
                    discrepancies.Add(new {
                        productId = p.Id,
                        productName = p.Title,
                        sqlQuantity = p.StockQuantity,
                        warehouseQuantity = warehouseQty,
                        discrepancyPercent = percentDiff,
                        priority = "Urgent"
                    });
                }

            }

            return JsonSerializer.Serialize(new {
                threshold = 40,
                count = discrepancies.Count,
                discrepancies
            });

        }
    }
}
