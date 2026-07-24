using Bulky.DataAccess.AI.Inventory.Interfaces;
using Bulky.DataAccess.AI.Inventory.Messages;
using Bulky.DataAccess.AI.Inventory.Models;
using DocumentFormat.OpenXml.Bibliography;
using DocumentFormat.OpenXml.Drawing;
using DocumentFormat.OpenXml.ExtendedProperties;
using DocumentFormat.OpenXml.Spreadsheet;
using MassTransit;
using MassTransit.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.ConstrainedExecution;

namespace Bulky.DataAccess.AI.Inventory.Services
{
    public class InventoryOrchestrationService : IInventoryOrchestrator
    {
        private readonly IInventoryReader _inventoryReader;
        private readonly IWarehouseReader _warehouseReader;

        private readonly IInventoryAgentFactory _agentFactory;
        private readonly IPublishEndpoint _publishEndpoint;
        private readonly ILogger<InventoryOrchestrationService> _logger;

        public InventoryOrchestrationService(
            IInventoryReader inventoryReader,
            IWarehouseReader warehouseReader,
            IInventoryAgentFactory agentFactory,
            IPublishEndpoint publishEndpoint,
            ILogger<InventoryOrchestrationService> logger)
        {
            _inventoryReader = inventoryReader;
            _warehouseReader = warehouseReader;
            _agentFactory = agentFactory;
            _publishEndpoint = publishEndpoint;
            _logger = logger;
        }


        //The orchestrator runs a five-step process:
        //Step 1: read warehouse data(IWarehouseReader)
        //Step 2: deterministic reconciliation scan(GetReconciliationResult)
        //Step 3: publish events per item(LowStockDetected or StockDiscrepancyDetected)
        //Step 4: MAF group-chat briefing(ExcelAgent + ReconciliationAgent)
        //Step 5: MAF EmailAgent for urgent events(in try/catch — graceful fallback)
        public async Task<InventoryCheckResult> RunInventoryCheckAsync(CancellationToken cancellationToken = default)
        {
            // ---- Step 1: read warehouse data (may be empty if blob unavailable) ----
            IReadOnlyList<WarehouseStockItem> warehouseItems = [];

            try {

                warehouseItems = await _warehouseReader.ReadAllAsync(cancellationToken);

            } catch(Exception ex) {

                // A missing blob returns [] (handled in the reader); this catch covers
                // network/auth failures. Either way, discrepancy detection is suppressed
                // for this run and only low-stock events fire — graceful degradation.
                _logger.LogWarning(ex,
                    "[Inventory] Warehouse blob read failed — discrepancy detection suppressed");

            }


            // ---- Step 2: deterministic reconciliation scan ----
            var scan = _inventoryReader.GetReconciliationResult(warehouseItems);

            // ---- Step 3: deterministic publish — one event per reconciliation item ----
            var published = 0;

            foreach(var item in scan.Items) { 
            
                if(item.EventType == ReconciliationEventType.UrgentDiscrepancy) {
                    await _publishEndpoint.Publish(
                        
                        new StockDiscrepancyDetected(
                            item.ProductId, 
                            item.ProductName, 
                            item.SqlQuantity,
                            item.WarehouseQuantity,
                            item.DiscrepancyPercentage,
                            "Urgent"), 
                        cancellationToken);

                    published++;

                } else {
                    var priority = item.SqlQuantity == 0 ? "Urgent" : "Routine";
                    
                    await _publishEndpoint.Publish(
                        new LowStockDetected(
                            item.ProductId, 
                            item.ProductName, 
                            item.SqlQuantity, 
                            InventoryReader.LowStockThreshold,
                            priority), 
                        cancellationToken);

                    published++;
                }

            }

            _logger.LogInformation(
                    "[Inventory] Published {Count} event(s) — {Low} low-stock, " +
                    "{Urgent} urgent discrepancies",
                    published, scan.LowStockCount, scan.UrgentDiscrepancyCount);

            // ---- Step 4: MAF group-chat reconciliation briefing ----

            //Despite the word "reconciliation" in the name, this step does not reconcile anything.
            //The actual reconciliation already happened deterministically in Step 2(GetReconciliationResult) 
            //That's where SQL vs warehouse quantities get compared, discrepancy percentages get calculated,
            //and each item gets classified as low-stock or urgent-discrepancy.
            //All the event routing in Step 3 fired off that deterministic result.
            //By the time you reach Step 4, every decision that matters is already made and every event is already published.
            //So the briefing is pure narration — it describes what the deterministic scan found, in prose.
            var briefing = await BuildReconciliationBriefingAsync(scan, warehouseItems, cancellationToken);

            // ---- Step 5: email alert for urgent items (via EmailAgent) ----
            if(scan.UrgentDiscrepancyCount > 0) {

                await SendEmailAlertAsync(scan, briefing, cancellationToken);

            }

            return new InventoryCheckResult(
                                            LowStockCount: scan.LowStockCount,
                                            AlertsPublished: published,
                                            Briefing: briefing);

        }



        //The multi-agent layer workflow. Sequential workflow: the SQL agent calls its
        //tool and reports findings; then the notification agent reads the SQL agent's
        //output and produces a briefing.
        private async Task<string> BuildReconciliationBriefingAsync(ReconciliationResult scan,
                                        IReadOnlyList<WarehouseStockItem> warehouseItems,
                                        CancellationToken ct) {

            try {

                AIAgent sqlAgent = _agentFactory.CreateSqlAgent();
                AIAgent excelAgent = _agentFactory.CreateExcelAgent();
                AIAgent reconciliationAgent = _agentFactory.CreateReconciliationAgent();

                Workflow workflow = AgentWorkflowBuilder
                    .CreateGroupChatBuilderWith(agents =>
                        new RoundRobinGroupChatManager(agents) {
                            MaximumIterationCount = 4
                        })
                    .AddParticipants(new AIAgent[] { sqlAgent, excelAgent, reconciliationAgent })
                    .Build();

                List<ChatMessage> input =
                [
                    new(ChatRole.User,
                "Run the scheduled inventory reconciliation. " +
                "Compare SQL quantities against warehouse quantities " +
                "and produce a reconciliation report for the administrator.")
                ];

                await using StreamingRun run = await InProcessExecution
                                                .RunStreamingAsync(workflow, input, cancellationToken: ct);

                await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

                string briefing = string.Empty;

                await foreach(WorkflowEvent workEvent in run.WatchStreamAsync()) {

                    _logger.LogInformation(
                        "[Inventory] Briefing stream event: {Type} | IsOutput={IsOut}",
                        workEvent.GetType().Name,
                        workEvent is WorkflowOutputEvent);

                    if(workEvent is WorkflowOutputEvent outputEvent
                       && outputEvent.As<List<ChatMessage>>() is { Count: > 0 } msgs) {

                        briefing = msgs.Last().Text ?? string.Empty;

                        _logger.LogInformation(
                            "[Inventory] ReconciliationAgent output: {Output}", briefing);

                        break;
                    }

                    if(workEvent is ExecutorFailedEvent failedEvt) {
                        _logger.LogWarning("[Inventory] Briefing executor failed: {Details}", failedEvt.ToString());
                        break;
                    }
                }

                _logger.LogInformation("[Inventory] Reconciliation briefing: {Briefing}", briefing);

                return string.IsNullOrWhiteSpace(briefing) ?
                    DeterministicReconciliationSummary(scan)
                    : briefing.Trim();

            } catch(Exception ex) {
                _logger.LogWarning(ex,
                    "[Inventory] Reconciliation briefing failed — using deterministic summary");
                return DeterministicReconciliationSummary(scan);
            }
        }


        public async Task SendEmailAlertAsync(
                                        ReconciliationResult scan,
                                        string briefing,
                                        CancellationToken ct) {
            _logger.LogInformation("[Inventory] SendEmailAlertAsync BUILD MARKER v3-payload-guard");
            try {

                AIAgent emailAgent = _agentFactory.CreateEmailAgent();

                Workflow emailWorkflow = AgentWorkflowBuilder
                    .BuildSequential(new AIAgent[] { emailAgent });

                List<ChatMessage> input =
                [
                    new(ChatRole.User,
                $"Send an urgent stock alert email. " +
                $"There are {scan.UrgentDiscrepancyCount} urgent discrepancies. " +
                $"Briefing: {briefing}")
                ];

                await using StreamingRun run = await InProcessExecution
                                                .RunStreamingAsync(emailWorkflow, input, cancellationToken: ct);

                await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

                bool completed = false;

                await foreach(WorkflowEvent evt in run.WatchStreamAsync()) {

                    _logger.LogInformation(
                        "[Inventory] EmailAgent stream event: {Type} | IsOutput={IsOut} | Base={Base}",
                        evt.GetType().Name,
                        evt is WorkflowOutputEvent,
                        evt.GetType().BaseType?.Name);

                    // Match on the payload, not just the type. A derived event such as
                    // AgentResponseUpdateEvent may satisfy `is WorkflowOutputEvent` while
                    // carrying a streaming delta rather than the final message list —
                    // breaking on it abandons the run before the tool call completes.
                    if(evt is WorkflowOutputEvent outputEvt
                       && outputEvt.As<List<ChatMessage>>() is { Count: > 0 } msgs) {

                        completed = true;

                        foreach(var msg in msgs) {
                            var contentTypes = string.Join(", ", msg.Contents.Select(c => c.GetType().Name));
                            _logger.LogInformation(
                                "[Inventory] EmailAgent message — Role: {Role} | ContentTypes: {Types} | Text: {Text}",
                                msg.Role, contentTypes, msg.Text);
                        }

                        _logger.LogInformation(
                            "[Inventory] EmailAgent completed for {Urgent} urgent discrepancy items",
                            scan.UrgentDiscrepancyCount);

                        break;
                    }

                    if(evt is ExecutorFailedEvent failedEvt) {
                        _logger.LogWarning("[Inventory] EmailAgent executor failed: {Details}", failedEvt.ToString());
                        break;
                    }
                }

                if(!completed) {
                    _logger.LogWarning(
                        "[Inventory] EmailAgent stream ended without a usable WorkflowOutputEvent — email may not have been sent");
                }

            } catch(Exception ex) {

                _logger.LogWarning(ex, "[Inventory] EmailAgent failed — administrator not notified");

            }
        }

        private static string DeterministicReconciliationSummary(ReconciliationResult scan) {
            
            if(scan.Items.Count == 0)
                return "Inventory and warehouse data are in sync — no action needed.";
            var parts = new List<string>();
            if(scan.LowStockCount > 0)
                parts.Add($"{scan.LowStockCount} product(s) are below the stock threshold");
            if(scan.UrgentDiscrepancyCount > 0)
                parts.Add($"{scan.UrgentDiscrepancyCount} urgent SQL-vs-warehouse discrepanc(ies) detected");

            return string.Join("; ", parts) + ".";

        }

    }
}
