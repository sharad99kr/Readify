using Bulky.DataAccess.AI.Inventory.Messages;
using DocumentFormat.OpenXml.Spreadsheet;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.CodeAnalysis.Elfie.Serialization;
using ProjectCore.Hubs;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ProjectCore.Consumers
{
    public class DeadLetterConsumer<TEvent> : IConsumer<Fault<TEvent>>
                                                    where TEvent : class
    {
        private readonly ILogger<DeadLetterConsumer<TEvent>> _logger;

        public DeadLetterConsumer(ILogger<DeadLetterConsumer<TEvent>> logger)
        {
            _logger = logger;
        }

        public Task Consume(ConsumeContext<Fault<TEvent>> context)
        {
            var fault = context.Message;
            _logger.LogError(
                "[DeadLetter] Message of type {EventType} failed after all retries. " +
                "Conversation ID: {ConversationId}. " +
                "Exceptions: {Exceptions}",
                typeof(TEvent).Name,
                fault.FaultedMessageId,
                string.Join(", ", fault.Exceptions.Select(e => e.Message))
            );
            //We can persist the fault information to a database for further analysis or
            //admin dashboard can surface dead - letter rate trends.
            return Task.CompletedTask;
        }
    }
}
