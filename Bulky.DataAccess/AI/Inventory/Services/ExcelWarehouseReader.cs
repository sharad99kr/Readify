using Azure;
using Azure.Storage.Blobs;
using Bulky.DataAccess.AI.Inventory.Interfaces;
using ClosedXML.Excel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Bulky.DataAccess.AI.Inventory.Services
{
    public class ExcelWarehouseReader : IWarehouseReader
    {
        private readonly BlobContainerClient _container;
        private readonly string _blobName;
        private readonly ILogger<ExcelWarehouseReader> _logger;

        public ExcelWarehouseReader(BlobServiceClient blobServiceClient, IConfiguration config, ILogger<ExcelWarehouseReader> logger) {
            
            var containerName = config["Inventory:WarehouseContainer"] ?? "inventory-data";
            _blobName = config["Inventory:WarehouseBlobName"] ?? "warehouse_stock.xlsx";
            _container = blobServiceClient.GetBlobContainerClient(containerName);
            _logger = logger;
        }
        public async Task<IReadOnlyList<WarehouseStockItem>> ReadAllAsync(CancellationToken cancellationToken = default) {
            var blobClient = _container.GetBlobClient(_blobName);

            using var stream = new MemoryStream();
            try {

                await blobClient.DownloadToAsync(stream, cancellationToken);
            
            } catch(RequestFailedException ex) when (ex.Status==404) {

                _logger.LogWarning(
                    "[WarehouseReader] Blob {Container}/{Blob} not found — returning empty list",
                       _container.Name, _blobName);
                return [];
                
            }

            stream.Position = 0;

            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.First();
            var items = new List<WarehouseStockItem>();

            foreach(var row in sheet.RowsUsed().Skip(1)) {
                if(!int.TryParse(row.Cell(1).GetString(), out int productId)) {
                    _logger.LogWarning(
                        "[WarehouseReader] Invalid ProductId '{ProductId}' in row {RowNumber} — skipping",
                        row.Cell(1).GetString(), row.RowNumber());
                    continue;
                }

                var name = row.Cell(2).GetString();
                if(string.IsNullOrWhiteSpace(name)) {
                    _logger.LogWarning(
                        "[WarehouseReader] Empty ProductName in row {RowNumber} — skipping",
                        row.RowNumber());
                    continue;
                }

                if(!int.TryParse(row.Cell(3).GetString(), out int quantity)) {
                    _logger.LogWarning(
                        "[WarehouseReader] Invalid WarehouseQuantity '{Quantity}' in row {RowNumber} — skipping",
                        row.Cell(3).GetString(), row.RowNumber());
                    continue;
                }

                items.Add(new WarehouseStockItem(productId, name, quantity));
            }


            _logger.LogInformation(
                "[WarehouseReader] Read {Count} rows from {Container}/{Blob}",
                items.Count, _container.Name, _blobName);

            return items;
        }
    }
}
