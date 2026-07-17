using Azure.Search.Documents.KnowledgeBases.Models;
using Bulky.DataAccess;
using Bulky.DataAccess.Data;
using Bulky.DataAccess.DbInitializer;
using Bulky.DataAccess.Repository;
using Bulky.DataAccess.Repository.IRepository;
using Bulky.Utility;
using MassTransit;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ProjectCore;
using ProjectCore.Configuration;
using ProjectCore.Consumers;
using ProjectCore.Hubs;
using Stripe;
using System.Security.Authentication;
using System.Threading.RateLimiting;
using Bulky.DataAccess.AI.Inventory.Messages;   // StockDiscrepancyDetected, LowStockDetected


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews()
    .AddJsonOptions(options => {
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

//when we add something to service class we are adding it to dependency injection
//and telling the application to do this configuration whenever it is called for implementation
builder.Services.AddDbContext<Bulky.DataAccess.Data.ApplicationDbContext>(options => 
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"), 
        sqlOptions => sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null)));

builder.Services.Configure<StripeSettings>(builder.Configuration.GetSection("Stripe"));

builder.Services.AddIdentity<IdentityUser,IdentityRole>().AddEntityFrameworkStores<Bulky.DataAccess.Data.ApplicationDbContext>().AddDefaultTokenProviders(); //AddDefaultTokenProviders is needed for email confirmation token, generally default identity has implementation of default token providers

builder.Services.ConfigureApplicationCookie(options => {
    options.LoginPath = $"/Identity/Account/Login";
    options.LogoutPath = $"/Identity/Account/Logout";
    options.AccessDeniedPath = $"/Identity/Account/AccessDenied";
});

//facebook login configuration
builder.Services.AddAuthentication().AddFacebook(options => {
    options.AppId = "1303797191312940";
    options.AppSecret = "1f0baef726e3a8b183cd0258295d4318";
});

//sessions doesn't come by default in the basic project. It's configuration needs following setup
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options => {
    options.IdleTimeout=TimeSpan.FromMinutes(100);
    options.Cookie.HttpOnly= true;
    options.Cookie.IsEssential= true;
});

builder.Services.AddScoped<IDbInitializer, DbInitializer>();

builder.Services.AddRazorPages();//we need to notify when razor pages are present in the project(i.e. Identity pages)
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IEmailSender, EmailSender>();

//Rate limiting configuration for user semantic search
builder.Services.AddRateLimiter(options => {
    options.AddFixedWindowLimiter(policyName: "search", options => {
        options.PermitLimit = 10;
        options.Window = TimeSpan.FromSeconds(30);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 0;
    });
});


//Rate limiting configuration for admin semantic + azure compare search 
builder.Services.AddRateLimiter(options => {
    options.AddFixedWindowLimiter(policyName: "CompareSearch", options => {
        options.PermitLimit = 10;
        options.Window = TimeSpan.FromSeconds(30);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 0;
    });
});

//Rate limiting configuration for user chat 
builder.Services.AddRateLimiter(options => {
    options.AddFixedWindowLimiter(policyName: "chat", options => {
        options.PermitLimit = 20;
        options.Window = TimeSpan.FromMinutes(1);
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 2;
    });
});

//AI services
builder.Services.AddAIServices(builder.Configuration);

//MediatR
builder.Services.AddMediatR(cfg => {
    cfg.RegisterServicesFromAssemblyContaining<ProjectCoreAssemblyMarker>();   // handlers live here
    cfg.RegisterServicesFromAssemblyContaining<DataAccessAssemblyMarker>(); // commands live here
});

//MassTransit
var rabbitHost = builder.Configuration["RabbitMQ:Host"];
var rabbitVHost = builder.Configuration["RabbitMQ:VHost"];
var rabbitUser = builder.Configuration["RabbitMQ:Username"];
var rabbitPassword = builder.Configuration["RabbitMQ:Password"];

builder.Services.AddMassTransit(x => {

    // BulkyWeb hosts every consumer — they push SignalR, which only has
    // meaning in a process with connected browser clients.
    x.AddConsumer<NotificationConsumer>();
    x.AddConsumer<DiscrepancyConsumer>();
    x.AddConsumer<DeadLetterConsumer<StockDiscrepancyDetected>>();
    x.AddConsumer<DeadLetterConsumer<LowStockDetected>>();

    if(string.IsNullOrWhiteSpace(rabbitHost)) {

        // Unit tests / CI — zero infrastructure, in-process publish/subscribe.
        x.UsingInMemory((context, cfg) => {
            cfg.ConfigureEndpoints(context);
        });

    } else {
        // CloudAMQP RabbitMQ — accessible from local dev and Azure App Service.
        // TLS is mandatory on the CloudAMQP free tier (amqps:// only).
        x.UsingRabbitMq((context, cfg) => {
            cfg.Host(rabbitHost, 5671, rabbitVHost, h => {
                h.Username(rabbitUser);
                h.Password(rabbitPassword);
                h.UseSsl(s => s.Protocol = SslProtocols.Tls12);
            });

            cfg.ReceiveEndpoint("low-stock-queue", e => {
                // Exponential backoff, 3 retries. After retries are exhausted
                // MassTransit routes the message to an _error queue —
                // it is never silently lost.
                e.UseMessageRetry(r => r.Exponential(
                    retryLimit: 3,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(30),
                    intervalDelta: TimeSpan.FromSeconds(5)));

                e.ConfigureConsumer<NotificationConsumer>(context);
            });

            cfg.ReceiveEndpoint("discrepancy-queue", e => {
                e.UseMessageRetry(r => r.Exponential(
                    retryLimit: 3,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(30),
                    intervalDelta: TimeSpan.FromSeconds(5)));

                e.ConfigureConsumer<DiscrepancyConsumer>(context);
            });

            // Dead-letter queues — terminal handling after retries are exhausted.
            cfg.ReceiveEndpoint("discrepancy-fault-queue", e => {
                e.ConfigureConsumer<DeadLetterConsumer<StockDiscrepancyDetected>>(context);
            });

            cfg.ReceiveEndpoint("low-stock-fault-queue", e => {
                e.ConfigureConsumer<DeadLetterConsumer<LowStockDetected>>(context);
            });
        });
    }
});

builder.Services.AddSignalR();


var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
StripeConfiguration.ApiKey = builder.Configuration.GetSection("Stripe:SecretKey").Get<string>();
app.UseRouting();
app.UseAuthentication();//basically checking if user name and password is valid
app.UseAuthorization();//access to pages is restricted by roles
app.UseRateLimiter();//access to pages is restricted by rate limiter
app.UseSession();//access to configured session
//running migrations at the start of application to make sure database is up to date with the latest changes in code. This is not recommended for production environment but can be used in development environment for ease of use
using(var scope = app.Services.CreateScope()) {
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}
SeedDatabase();//calling initialize of database when application starts using scope
app.MapRazorPages();//this will make sure rounting is added to map the razor pages

app.MapHub<InventoryAlertHub>("/hubs/inventory-alerts");

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{area=Customer}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.UseExceptionHandler(a => a.Run(async ctx => {
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    await ctx.Response.WriteAsJsonAsync(new { error = ex?.Error.ToString() });
}));

app.Run();

void SeedDatabase() {
    using(var scope = app.Services.CreateScope()) {
        var dbInitializer = scope.ServiceProvider.GetRequiredService<IDbInitializer>();
        dbInitializer.Initialize();
    }
}