using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

/// <summary>
/// System-level tenant provisioning. Protected by a separate SYSTEM_SECRET,
/// not by the regular API key or admin tokens.
/// POST /api/system/seed — creates all tables, admin user, default roles, config, and categories.
/// </summary>
public class SystemSeedFunction
{
    private readonly AdminAuth _auth;
    private readonly TenantTableFactory _tables;
    private readonly PermissionChecker _permChecker;
    private readonly ILogger<SystemSeedFunction> _logger;
    private readonly string _systemSecret;

    public SystemSeedFunction(AdminAuth auth, TenantTableFactory tables,
        PermissionChecker permChecker, ILogger<SystemSeedFunction> logger)
    {
        _auth = auth;
        _tables = tables;
        _permChecker = permChecker;
        _logger = logger;
        // Use a separate secret — NOT the API key
        _systemSecret = Environment.GetEnvironmentVariable("SYSTEM_SECRET")
            ?? "lostdogtracer-system-secret-change-in-prod";
    }

    [Function("SystemSeed")]
    public async Task<HttpResponseData> Seed(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "system/seed")] HttpRequestData req)
    {
        // Validate system secret from header
        if (!req.Headers.TryGetValues("X-System-Secret", out var secretValues) ||
            secretValues.FirstOrDefault() != _systemSecret)
        {
            return req.CreateErrorResponse(HttpStatusCode.Forbidden, "Ungültiges System-Secret.");
        }

        var (body, bodyError) = await req.ReadJsonBodyAsync<SeedRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.TenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");
        if (string.IsNullOrWhiteSpace(body.AdminUsername))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "adminUsername ist erforderlich.");
        if (string.IsNullOrWhiteSpace(body.AdminPassword) || body.AdminPassword.Length < 8)
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "adminPassword muss mindestens 8 Zeichen haben.");

        try
        {
            var tenantId = body.TenantId.Trim();
            var adminUser = body.AdminUsername.Trim().ToLowerInvariant();
            var displayName = body.AdminDisplayName?.Trim() ?? body.AdminUsername.Trim();

            _logger.LogInformation("Seeding tenant: {TenantId}", tenantId);

            // 1. Create all 9 tables
            await _tables.EnsureTablesExistAsync(tenantId);

            // 2. Seed default roles (Helfer, Mitglied, Teamleiter, Administrator)
            await _permChecker.SeedDefaultRolesAsync(tenantId);

            // 3. Create admin user
            var usersTable = _tables.GetTableClient(tenantId, "Users");
            bool adminExists = false;
            try
            {
                await usersTable.GetEntityAsync<TableEntity>("users", adminUser);
                adminExists = true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }

            if (!adminExists)
            {
                var entity = new TableEntity("users", adminUser)
                {
                    { "DisplayName", InputSanitizer.StripHtml(displayName) },
                    { "PasswordHash", PasswordHasher.Hash(body.AdminPassword) },
                    { "RoleId", "admin" },
                    { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
                };
                await usersTable.AddEntityAsync(entity);
            }

            // 4. Seed default config
            var configTable = _tables.GetTableClient(tenantId, "Config");
            bool configExists = false;
            try
            {
                await configTable.GetEntityAsync<TableEntity>("config", "settings");
                configExists = true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }

            if (!configExists)
            {
                var configEntity = new TableEntity("config", "settings")
                {
                    { "SiteBanner", displayName },
                    { "GuestCategoryRowKey", "" },
                    { "PrivacyUrl", "docs/datenschutz.html" },
                    { "ImprintUrl", "docs/impressum.html" },
                    { "Doc1Label", "" }, { "Doc1Link", "" },
                    { "Doc2Label", "" }, { "Doc2Link", "" },
                    { "Doc3Label", "" }, { "Doc3Link", "" },
                    { "DebugLogin", "false" },
                    { "FeatDeployment", true },
                    { "FeatEquipment", true }
                };
                await configTable.AddEntityAsync(configEntity);
            }

            // 5. Seed default categories
            var catTable = _tables.GetTableClient(tenantId, "Categories");
            bool hasCats = false;
            await foreach (var _ in catTable.QueryAsync<TableEntity>(maxPerPage: 1))
            { hasCats = true; break; }

            int catCount = 0;
            if (!hasCats)
            {
                var defaults = new Dictionary<string, string>
                {
                    ["Flyer/Handzettel"] = @"<rect x=""7"" y=""5"" width=""10"" height=""13"" rx=""1"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><line x1=""9.5"" y1=""9"" x2=""14.5"" y2=""9"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""12"" x2=""14.5"" y2=""12"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""15"" x2=""12.5"" y2=""15"" stroke=""#fff"" stroke-width=""1.2""/>",
                    ["Sichtung"] = @"<ellipse cx=""12"" cy=""12"" rx=""6"" ry=""4"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""2"" fill=""#fff""/>",
                    ["Entlaufort"] = @"<circle cx=""9"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""15"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""7"" cy=""13"" r=""1.3"" fill=""#fff""/><circle cx=""17"" cy=""13"" r=""1.3"" fill=""#fff""/><ellipse cx=""12"" cy=""15"" rx=""3"" ry=""2.2"" fill=""#fff""/>",
                    ["Standort Falle"] = @"<circle cx=""12"" cy=""12"" r=""5"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""1.5"" fill=""#fff""/><line x1=""12"" y1=""5"" x2=""12"" y2=""8"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""12"" y1=""16"" x2=""12"" y2=""19"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""5"" y1=""12"" x2=""8"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""16"" y1=""12"" x2=""19"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/>"
                };
                foreach (var (name, svg) in defaults)
                {
                    var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
                    await Task.Delay(5); // ensure unique rowKeys
                    await catTable.AddEntityAsync(new TableEntity("categories", rowKey)
                    {
                        { "DisplayName", name },
                        { "SvgSymbol", svg }
                    });
                    catCount++;
                }
            }

            _logger.LogInformation("Tenant seeded: {TenantId} — admin={Admin}, roles=4, config={ConfigNew}, categories={CatCount}",
                tenantId, adminUser, !configExists, catCount);

            return req.CreateJsonResponse(HttpStatusCode.OK, new
            {
                message = $"Mandant '{tenantId}' erfolgreich angelegt.",
                tenantId,
                adminUsername = adminUser,
                adminCreated = !adminExists,
                tablesCreated = TenantTableFactory.TableNames.Length,
                rolesSeeded = 4,
                configCreated = !configExists,
                categoriesSeeded = catCount
            });
        }
        catch (ArgumentException ex)
        {
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding tenant {TenantId}", body.TenantId);
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Fehler beim Anlegen des Mandanten.");
        }
    }

    private record SeedRequest(string? TenantId, string? AdminUsername, string? AdminPassword, string? AdminDisplayName);
}
