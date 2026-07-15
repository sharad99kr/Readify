using Bulky.DataAccess.AI.Inventory.Messages;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using ProjectCore.Hubs;

namespace ProjectCore.Consumers
{
    public class DiscrepancyConsumer : IConsumer<StockDiscrepancyDetected>
    {
        private readonly ILogger<DiscrepancyConsumer> _logger;
        private readonly IHubContext<InventoryAlertHub> _hubContext;

        public DiscrepancyConsumer(ILogger<DiscrepancyConsumer> logger, IHubContext<InventoryAlertHub> hubContext)
        {
            _logger = logger;
            _hubContext = hubContext;
        }

        public async Task Consume(ConsumeContext<StockDiscrepancyDetected> context)
        {
            var message = context.Message;
            _logger.LogInformation("Received stock discrepancy for ProductId: {ProductId}, DiscrepancyPercent: {Discrepancy}", message.ProductId, message.DiscrepancyPercent);
            
            var alert = new InventoryAlert (
                ProductId: message.ProductId,
                ProductName: message.ProductName,
                Quantity: message.SQLQuantity,
                Priority: message.AlertPriority,
                Kind: "Discrepancy",
                Message: $"{message.ProductName}: SQL has {message.SQLQuantity} unit(s), " +
                             $"warehouse has {message.ExcelQuantity} unit(s) " +
                             $"({message.DiscrepancyPercent}% discrepancy).",
                TimestampUtc: DateTime.UtcNow
            );

            _logger.LogInformation(
               "[Consumer] StockDiscrepancyDetected -> SignalR — Product {Id}, " +
               "{Pct}% discrepancy, {Priority}",
               message.ProductId, message.DiscrepancyPercent, message.AlertPriority);

            // Send the discrepancy alert to connected clients via SignalR
            await _hubContext
                .Clients
                .All
                .SendAsync("ReceiveDiscrepancyAlert",alert, context.CancellationToken);
        }
    }
}
