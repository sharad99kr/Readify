using Bulky.DataAccess.AI.Inventory.Interfaces;
using Bulky.DataAccess.AI.Inventory.Messages;
using Bulky.DataAccess.AI.Inventory.Models;
using Bulky.DataAccess.AI.Inventory.Services;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using ProjectCore.CQRS.Queries;
using ProjectCore.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// These tests cover the DETERMINISTIC trigger — the business-critical path —
// without any LLM. The agent factory is set up to throw, so the briefing
// gracefully falls back; the publish (which runs first) is unaffected.
namespace Bulky.Tests.Services
{
    public class InventoryOrchestrationServiceTests
    {
        private static InventoryOrchestrationService Build(
                                        IReadOnlyList<InventoryStatusResult> lowStock,
                                        Mock<IPublishEndpoint> publish) 
        {
            var reader = new Mock<IInventoryReader>();
            reader.Setup(r => r.GetLowStockProducts()).Returns(lowStock);

            var warehouseReader = new Mock<IWarehouseReader>();
            warehouseReader
                .Setup(w => w.ReadAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var agentFactory = new Mock<IInventoryAgentFactory>();
            agentFactory.Setup(f => f.CreateSqlAgent())
                        .Throws(new InvalidOperationException("no LLM in unit test"));

            return new InventoryOrchestrationService(
                reader.Object,
                warehouseReader.Object,
                agentFactory.Object,
                publish.Object,
                Mock.Of<ILogger<InventoryOrchestrationService>>());
        }

        [Fact]
        public async Task RunInventoryCheck_PublishesOneEventPerLowStockProduct() {
            var publish = new Mock<IPublishEndpoint>();
            var sut = Build(new List<InventoryStatusResult>
            {
            new(1, "Book A", 0, true),   // zero => Urgent
            new(2, "Book B", 3, true),   // below threshold => Routine
        }, publish);

            var result = await sut.RunInventoryCheckAsync(CancellationToken.None);

            Assert.Equal(2, result.LowStockCount);
            Assert.Equal(2, result.AlertsPublished);

            publish.Verify(p => p.Publish(
                It.Is<LowStockDetected>(e => e.ProductId == 1 && e.AlertPriority == "Urgent"),
                It.IsAny<CancellationToken>()), Times.Once);

            publish.Verify(p => p.Publish(
                It.Is<LowStockDetected>(e => e.ProductId == 2 && e.AlertPriority == "Routine"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RunInventoryCheck_NoLowStock_PublishesNothing() {
            var publish = new Mock<IPublishEndpoint>();
            var sut = Build(new List<InventoryStatusResult>(), publish);

            var result = await sut.RunInventoryCheckAsync(CancellationToken.None);

            Assert.Equal(0, result.AlertsPublished);
            publish.Verify(p => p.Publish(
                It.IsAny<LowStockDetected>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
