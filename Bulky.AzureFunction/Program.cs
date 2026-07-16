using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Blobs;
using Bulky.DataAccess;
using Bulky.DataAccess.AI.CQRS.Commands;
using Bulky.DataAccess.AI.Inventory.Interfaces;
using Bulky.DataAccess.AI.Inventory.Messages;
using Bulky.DataAccess.AI.Inventory.Services;
using Bulky.DataAccess.Data;
using Bulky.DataAccess.Repository;
using Bulky.DataAccess.Repository.IRepository;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectCore;
using ProjectCore.Consumers;
using System.Security.Authentication;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((ctx, cfg) => {
        // For local dev, use local.settings.json (Functions v4 default).
        // For Azure: Application Settings are injected as env vars automatically.
        cfg.AddEnvironmentVariables();
    })
    .ConfigureServices((ctx, services) => {
        var cfg = ctx.Configuration;

        // EF Core — same connection string as BulkyWeb.
        services.AddDbContext<ApplicationDbContext>(o =>
            o.UseSqlServer(cfg.GetConnectionString("DefaultConnection"),
                sqlOptions => sqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null)));

        // Unit of Work + Repositories.
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // Inventory services.
        services.AddScoped<IInventoryReader, InventoryReader>();
        services.AddScoped<IInventoryOrchestrator, InventoryOrchestrationService>();

        // IChatClient for MAF agents (reuse same Azure OpenAI deployment).
        services.AddSingleton<IChatClient>(sp => {
            
            var endpoint = cfg["AzureOpenAI:Endpoint"]!;
            var deployment = cfg["AzureOpenAI:DeploymentName"]!;
            var apiKey = cfg["AzureOpenAI:ApiKey"];
            
            AzureOpenAIClient azureClient = string.IsNullOrEmpty(apiKey)
                ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
                : new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            
            return azureClient.GetChatClient(deployment)
                .AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();
        });

        // BlobServiceClient — warehouse data source .
        // Singleton: the client is thread-safe and connection-pooling.
        // Prefer a connection string when present (frictionless local dev);
        // otherwise account URI + DefaultAzureCredential (Managed Identity in
        // Azure, developer login locally) — same pattern as the AzureOpenAI client.
        services.AddSingleton<BlobServiceClient>(_ => {
            var blobConnectionString = cfg["Storage:ConnectionString"];
            if(!string.IsNullOrWhiteSpace(blobConnectionString)) {
                return new BlobServiceClient(blobConnectionString);
            }

            var accountUri = cfg["Storage:AccountUri"]!;
            return new BlobServiceClient(new Uri(accountUri), new DefaultAzureCredential());
        });

        services.AddScoped<IWarehouseReader, ExcelWarehouseReader>();
        services.AddScoped<IEmailAlertService, EmailAlertService>();

        // IInventoryAgentFactory (after Day 2 agents are built).
        services.AddScoped<IInventoryAgentFactory, InventoryAgentFactory>();

        // MediatR — scans the assembly for handlers.
        services.AddMediatR(mt => {
            mt.RegisterServicesFromAssemblyContaining<TriggerInventoryCheckCommand>();
        });

        // MassTransit — same CloudAMQP config as BulkyWeb.
        var rabbitHost = cfg["RabbitMQ:Host"];
        var rabbitVHost = cfg["RabbitMQ:VHost"];
        var rabbitUser = cfg["RabbitMQ:Username"];
        var rabbitPassword = cfg["RabbitMQ:Password"];

        services.AddMassTransit(x => {

            
            // BulkyWeb hosts the consumers (NotificationConsumer,
            // DiscrepancyConsumer, DeadLetterConsumer).
            x.AddConsumer<NotificationConsumer>();
            x.AddConsumer<DiscrepancyConsumer>();
            x.AddConsumer<DeadLetterConsumer<StockDiscrepancyDetected>>();
            x.AddConsumer<DeadLetterConsumer<LowStockDetected>>();

            if(string.IsNullOrWhiteSpace(rabbitHost)) {

                x.UsingInMemory((context, cfg) =>
                {
                    cfg.ConfigureEndpoints(context);
                });

            } else {

                x.UsingRabbitMq((context, cfg) => {

                    cfg.Host(rabbitHost, rabbitVHost, h => {
                        h.Username(rabbitUser);
                        h.Password(rabbitPassword);
                        h.UseSsl(s => s.Protocol = SslProtocols.Tls12);
                    });

                    cfg.ReceiveEndpoint("low-stock-queue", e => {
                        e.UseMessageRetry(r => r.Exponential(
                               retryLimit: 3,
                               minInterval: TimeSpan.FromSeconds(1),
                               maxInterval: TimeSpan.FromSeconds(30),
                               intervalDelta: TimeSpan.FromSeconds(5)));
                        e.ConfigureConsumer<NotificationConsumer>(context);
                    });

                    // Discrepancy queue for stock discrepancies.
                    cfg.ReceiveEndpoint("discrepancy-queue", e => {
                        e.UseMessageRetry(r => r.Exponential(
                               retryLimit: 3,
                               minInterval: TimeSpan.FromSeconds(1),
                               maxInterval: TimeSpan.FromSeconds(30),
                               intervalDelta: TimeSpan.FromSeconds(5)));
                        e.ConfigureConsumer<DiscrepancyConsumer>(context);
                    });

                    // Dead-letter queues for failed messages.
                    cfg.ReceiveEndpoint("discrepancy-fault-queue", e => {
                        e.ConfigureConsumer<DeadLetterConsumer<StockDiscrepancyDetected>>(context);
                    });
                });

            }
        });
    })
    .Build();

await host.RunAsync();
