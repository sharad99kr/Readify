using Bulky.DataAccess.AI.CQRS.Commands;
using Bulky.DataAccess.AI.Inventory.Interfaces;
using MediatR;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Bulky.AzureFunction;

public class InventoryCheckFunction
{
    private readonly ILogger<InventoryCheckFunction> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public InventoryCheckFunction(ILogger<InventoryCheckFunction> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    // Runs every hour at minute 0 (:00).
    // NCRONTAB for Azure Functions: "0 0 * * * *"
    // For testing locally, change to "0 */5 * * * *" (every 5 minutes).
    [Function("InventoryCheckFunction")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo myTimer, CancellationToken ct)
    {
        _logger.LogInformation(
              "[AzureFunction] InventoryCheckFunction fired at {Time}", DateTime.UtcNow);

        // WHY IServiceScopeFactory?
        // Azure Functions resolve constructor parameters from the host container,
        // which is a Singleton scope. IInventoryOrchestrator is Scoped (it
        // transitively depends on IUnitOfWork, which is Scoped). Injecting a
        // Scoped service directly into a Singleton is a captive-dependency bug.
        // IServiceScopeFactory is safe to inject into any lifetime, and each
        // invocation creates a fresh scope — correct for per-trigger execution.
        await using var scope = _scopeFactory.CreateAsyncScope();

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        try {
            InventoryCheckResult result = await mediator.Send(new TriggerInventoryCheckCommand(), ct);

            _logger.LogInformation(
                "[AzureFunction] Completed — {Low} low-stock, " +
                "{Published} event(s) published",
                result.LowStockCount, result.AlertsPublished);

        } catch(Exception ex) {

            // Log but do not rethrow — Azure Functions retry on exception and
            // could flood the broker. Graceful failure is preferred; the next
            // scheduled run will retry.
            _logger.LogError(ex, "[AzureFunction] InventoryCheckFunction failed");
        }
    }
}