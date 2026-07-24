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
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        services.Configure<LoggerFilterOptions>(options => {
            var defaultRule = options.Rules.FirstOrDefault(rule =>
                rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if(defaultRule is not null)
                options.Rules.Remove(defaultRule);
        });

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

            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions {
                ExcludeVisualStudioCredential = true,
                ExcludeVisualStudioCodeCredential = true,
                ExcludeAzurePowerShellCredential = true,
                ExcludeWorkloadIdentityCredential = true
            });

            var accountUri = cfg["Storage:AccountUri"]!;
            return new BlobServiceClient(new Uri(accountUri), credential);
        });

        services.AddScoped<IWarehouseReader, ExcelWarehouseReader>();
        services.AddScoped<IEmailAlertService, EmailAlertService>();

        // IInventoryAgentFactory (after Day 2 agents are built).
        services.AddScoped<IInventoryAgentFactory, InventoryAgentFactory>();

        // MediatR — scans the assembly for handlers.
        services.AddMediatR(mt => {
            mt.RegisterServicesFromAssemblyContaining<TriggerInventoryCheckCommand>();
        });


        // MassTransit — publish-only. The Function raises StockDiscrepancyDetected
        // and LowStockDetected; BulkyWeb hosts every consumer (they push SignalR,
        // which has no meaning in a Functions host).
        var rabbitHost = cfg["RabbitMQ:Host"];
        var rabbitVHost = cfg["RabbitMQ:VHost"];
        var rabbitUser = cfg["RabbitMQ:Username"];
        var rabbitPassword = cfg["RabbitMQ:Password"];

        services.AddMassTransit(x =>
        {
            if(string.IsNullOrWhiteSpace(rabbitHost)) {
                x.UsingInMemory((context, busCfg) => busCfg.ConfigureEndpoints(context));
            } else {
                x.UsingRabbitMq((context, busCfg) =>
                {
                    busCfg.Host(rabbitHost, 5671, rabbitVHost, h =>
                    {
                        h.Username(rabbitUser);
                        h.Password(rabbitPassword);
                        h.UseSsl(s => s.Protocol = SslProtocols.Tls12);
                    });
                    // No ReceiveEndpoints: this host does not consume.
                });
            }
        });
    })
    .Build();

await host.RunAsync();
