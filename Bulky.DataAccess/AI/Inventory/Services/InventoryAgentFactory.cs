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

        private const string SqlAgentInstructions =
            "You are the Inventory SQL Agent. Call get_low_stock_products to get " +
            "the list of products at or below the stock threshold. Report each " +
            "low product with its title and current quantity as a short list. " +
            "Never invent products. If the list is empty, say inventory is healthy.";

        private const string NotificationAgentInstructions =
            "You are the Notification Agent. Read the SQL agent's findings and " +
            "write a brief, plain-language briefing for a store administrator. " +
            "State how many products are low and name the ones to reorder first. " +
            "Keep it under 120 words. Do not add any product the SQL agent did " +
            "not mention.";


        private readonly IChatClient _chatClient;
        private readonly IInventoryReader _inventoryReader;

        private readonly IWarehouseReader _warehouseReader;
        private readonly IEmailAlertService _emailService;

        public InventoryAgentFactory(
                        IChatClient chatClient, 
                        IInventoryReader inventoryReader,
                        IWarehouseReader warehouseReader,
                        IEmailAlertService emailAlertService) {

            _chatClient = chatClient;
            _inventoryReader = inventoryReader;
            _warehouseReader = warehouseReader;
            _emailService = emailAlertService;
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

        public AIAgent CreateEmailAgent() =>
        new ChatClientAgent(
            _chatClient,
            name: "EmailAgent",
            instructions: EmailAgentInstructions,
            tools: [ AIFunctionFactory.Create(
                                _emailService.SendAlertEmailAsync,
                                new AIFunctionFactoryOptions { Name = "send_alert_email" }) ]);
        public AIAgent CreateReconciliationAgent() {
            return new ChatClientAgent(
                _chatClient,
                name: "ReconciliationAgent",
                instructions: ReconciliationAgentInstructions);
        }
        public AIAgent CreateExcelAgent() {
            return new ChatClientAgent(
                _chatClient,
                name: "InventoryExcelAgent",
                instructions: ExcelAgentInstructions,
                // AIFunctionFactory supports async (Task-returning) methods and strips
                // the CancellationToken from the model-facing schema. Name it explicitly
                // so the tool surfaces as get_warehouse_stock (matches the instructions).
                tools: [AIFunctionFactory.Create(
                        _warehouseReader.ReadAllAsync,
                        new AIFunctionFactoryOptions{ Name = "get_warehouse_stock"}
                    )]
                );
        }
    }
}
