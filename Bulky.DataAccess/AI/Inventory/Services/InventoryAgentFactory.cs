using Bulky.DataAccess.AI.Inventory.Interfaces;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Bulky.DataAccess.AI.Inventory.Services
{
    public class InventoryAgentFactory : IInventoryAgentFactory
    {
        private const string ExcelAgentInstructions =
               "You are the Warehouse Excel Agent. Call get_warehouse_stock to get " +
               "the list of products and their warehouse quantities from the Excel " +
               "warehouse file. Report each product with its warehouse quantity as a " +
               "short list. Never invent quantities. If the list is empty, say the " +
               "warehouse data source is unavailable.";

        private const string ReconciliationAgentInstructions =
            "You are the Reconciliation Agent. You receive the SQL quantities from " +
            "the SQL Agent and the warehouse quantities from the Excel Agent. " +
            "Identify where SQL and warehouse quantities diverge by more than 40%. " +
            "Classify each discrepancy as Urgent (>40%) or Routine (<= 40% or low " +
            "stock only). Write a short, plain-language reconciliation report for the " +
            "store administrator — under 150 words. State the count of urgent " +
            "discrepancies and name the specific products. Do not invent data.";

        private const string EmailAgentInstructions =
            "You are the Email Agent. When you receive a reconciliation report " +
            "containing Urgent discrepancies, call send_alert_email to notify the " +
            "store administrator. Compose a concise subject line and a brief email " +
            "body (under 100 words) summarising the urgent items. If there are no " +
            "Urgent discrepancies, do not send any email.";

        private readonly IWarehouseReader _warehouseReader;
        private readonly IEmailAlertService _emailService;

        public InventoryAgentFactory(IChatClient chatClient, IInventoryReader inventoryReader) {
            _chatClient = chatClient;
            _inventoryReader = inventoryReader;
        }

        public AIAgent CreateSqlAgent() {
            return new ChatClientAgent(_chatClient,
                name: "InventorySqlAgent",
                instructions: SqlAgentInstructions,
                tools: [AIFunctionFactory.Create(_inventoryReader.GetLowStockProducts)]);
        }

        public AIAgent CreateNotificationAgent() {
            return new ChatClientAgent(
                _chatClient,
                name: "InventoryNotificationAgent",
                instructions: NotificationAgentInstructions);
        }
    }
}
