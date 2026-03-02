using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using FlyerTracker.Api.Security;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var connectionString = context.Configuration["STORAGE_CONNECTION_STRING"]
            ?? "UseDevelopmentStorage=true";

        services.AddSingleton(new TableServiceClient(connectionString));
        services.AddSingleton(new BlobServiceClient(connectionString));

        // ── Security services ────────────────────────────────────
        // API Key — protects all endpoints against scanners
        var apiKey = context.Configuration["API_KEY"] ?? "flyertracker-dev-key-2026";
        services.AddSingleton(new ApiKeyValidator(apiKey));

        // Rate Limiter — max 10 save-location calls per minute per IP
        services.AddSingleton(new RateLimiter(maxRequests: 10, window: TimeSpan.FromMinutes(1)));

        // Admin Auth — multi-user, Table Storage backed, PBKDF2 hashed
        var tokenSecret = context.Configuration["TOKEN_SECRET"] ?? "ft-local-dev-secret-key-change-in-prod";
        var seedUser = context.Configuration["ADMIN_SEED_USERNAME"] ?? "admin";
        var seedPass = context.Configuration["ADMIN_SEED_PASSWORD"] ?? "FlyerTracker2026!";
        services.AddSingleton(sp =>
        {
            var tableService = sp.GetRequiredService<TableServiceClient>();
            return new AdminAuth(tableService, tokenSecret, seedUser, seedPass);
        });
    })
    .Build();

host.Run();
