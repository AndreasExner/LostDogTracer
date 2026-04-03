using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var connectionString = context.Configuration["STORAGE_CONNECTION_STRING"]
            ?? "UseDevelopmentStorage=true";

        // ── Storage clients ──────────────────────────────────────
        services.AddSingleton(new TableServiceClient(connectionString));
        services.AddSingleton(new BlobServiceClient(connectionString));

        // ── Tenant table factory ─────────────────────────────────
        services.AddSingleton<TenantTableFactory>();

        // ── Security services ────────────────────────────────────

        // API Key — protects all endpoints
        var apiKey = context.Configuration["API_KEY"] ?? "lostdogtracer-dev-key-2026";
        services.AddSingleton(new ApiKeyValidator(apiKey));

        // Rate Limiter — Read: 120/min, Write: 15/min, Auth: 10/min per IP
        services.AddSingleton(new RateLimitProvider());

        // RBAC Permission Checker
        services.AddSingleton<PermissionChecker>();

        // Admin Auth — multi-tenant, HMAC tokens, PBKDF2 passwords
        var tokenSecret = context.Configuration["TOKEN_SECRET"] ?? "ft-local-dev-secret-key-change-in-prod";
        var seedUser = context.Configuration["ADMIN_SEED_USERNAME"] ?? "admin";
        var seedPass = context.Configuration["ADMIN_SEED_PASSWORD"] ?? "LostDogTracer2026!";
        services.AddSingleton(sp =>
        {
            var tables = sp.GetRequiredService<TenantTableFactory>();
            var permChecker = sp.GetRequiredService<PermissionChecker>();
            return new AdminAuth(tables, permChecker, tokenSecret, seedUser, seedPass);
        });
    })
    .Build();

host.Run();
