using System.Text.Json;
using Azure.Data.Tables;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class ConfigFunction
{
    private const string TableName = "Config";
    private const string PK = "config";
    private const string RK = "settings";

    private readonly TableServiceClient _tableService;
    private readonly ILogger<ConfigFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;
    private readonly RateLimitProvider _rateLimit;

    public ConfigFunction(TableServiceClient tableService, ILogger<ConfigFunction> logger,
        ApiKeyValidator apiKey, AdminAuth adminAuth, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
        _rateLimit = rateLimit;
    }

    /// <summary>Public endpoint — returns config values needed by all pages.</summary>
    [Function("GetConfig")]
    public async Task<IActionResult> GetConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };

            var entity = await GetOrSeedConfigAsync();

            return new OkObjectResult(new
            {
                siteBanner = entity.GetString("SiteBanner") ?? "LostDogTracer",
                guestCategoryRowKey = entity.GetString("GuestCategoryRowKey") ?? "",
                privacyUrl = entity.GetString("PrivacyUrl") ?? "",
                imprintUrl = entity.GetString("ImprintUrl") ?? "",
                doc1Label = entity.GetString("Doc1Label") ?? "",
                doc1Link = entity.GetString("Doc1Link") ?? "",
                doc2Label = entity.GetString("Doc2Label") ?? "",
                doc2Link = entity.GetString("Doc2Link") ?? "",
                doc3Label = entity.GetString("Doc3Label") ?? "",
                doc3Link = entity.GetString("Doc3Link") ?? "",
                debugLogin = string.Equals(entity.GetString("DebugLogin") ?? "", "true", StringComparison.OrdinalIgnoreCase),
                featDeployment = GetBoolSafe(entity, "FeatDeployment", true),
                featEquipment = GetBoolSafe(entity, "FeatEquipment", true)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading config");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Admin endpoint — update config values.</summary>
    [Function("UpdateConfig")]
    public async Task<IActionResult> UpdateConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/config")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 3) == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var table = _tableService.GetTableClient(TableName);

            var entity = await GetOrSeedConfigAsync();

            if (body.TryGetProperty("siteBanner", out var bannerProp))
                entity["SiteBanner"] = bannerProp.GetString()?.Trim() ?? "";
            if (body.TryGetProperty("guestCategoryRowKey", out var catProp))
                entity["GuestCategoryRowKey"] = catProp.GetString()?.Trim() ?? "";
            if (body.TryGetProperty("privacyUrl", out var privProp))
                entity["PrivacyUrl"] = privProp.GetString()?.Trim() ?? "";
            if (body.TryGetProperty("imprintUrl", out var impProp))
                entity["ImprintUrl"] = impProp.GetString()?.Trim() ?? "";
            foreach (var docField in new[] { "Doc1Label", "Doc1Link", "Doc2Label", "Doc2Link", "Doc3Label", "Doc3Link" })
            {
                var camel = char.ToLowerInvariant(docField[0]) + docField[1..];
                if (body.TryGetProperty(camel, out var docProp))
                    entity[docField] = docProp.GetString()?.Trim() ?? "";
            }
            if (body.TryGetProperty("featDeployment", out var fdProp))
                entity["FeatDeployment"] = fdProp.GetBoolean();
            if (body.TryGetProperty("featEquipment", out var feProp))
                entity["FeatEquipment"] = feProp.GetBoolean();

            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            _logger.LogInformation("Config updated");

            return new OkObjectResult(new { message = "Konfiguration gespeichert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating config");
            return new StatusCodeResult(500);
        }
    }

    private static readonly Dictionary<string, string> DefaultValues = new()
    {
        ["SiteBanner"] = "Mein Org Name hier",
        ["GuestCategoryRowKey"] = "001772623834586",
        ["PrivacyUrl"] = "docs/datenschutz.html",
        ["ImprintUrl"] = "docs/impressum.html",
        ["Doc1Label"] = "Einrichtung und erste Schritte",
        ["Doc1Link"] = "docs/LostDogTracer-1-Einrichtung_und_erste_Schritte.pdf",
        ["Doc2Label"] = "Benutzer Handbuch",
        ["Doc2Link"] = "docs/LostDogTracer-2-Benutzer_Handbuch.pdf",
        ["Doc3Label"] = "Admin Handbuch",
        ["Doc3Link"] = "docs/LostDogTracer-3-Admin_Handbuch.pdf",
        ["DebugLogin"] = "false",
        ["FeatDeployment"] = "true",
        ["FeatEquipment"] = "true"
    };

    private async Task<TableEntity> GetOrSeedConfigAsync()
    {
        var table = _tableService.GetTableClient(TableName);
        await table.CreateIfNotExistsAsync();

        try
        {
            var response = await table.GetEntityAsync<TableEntity>(PK, RK);
            var entity = response.Value;

            // Backfill missing fields with defaults
            bool updated = false;
            foreach (var (key, defaultVal) in DefaultValues)
            {
                if (!entity.ContainsKey(key) || string.IsNullOrEmpty(entity.GetString(key)))
                {
                    entity[key] = defaultVal;
                    updated = true;
                }
            }
            if (updated)
                await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);

            return entity;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            var entity = new TableEntity(PK, RK);
            foreach (var (key, val) in DefaultValues)
                entity[key] = val;
            await table.AddEntityAsync(entity);
            return entity;
        }
    }

    /// <summary>Read a boolean from a TableEntity, handling both native bool and string representations.</summary>
    private static bool GetBoolSafe(TableEntity entity, string key, bool defaultValue)
    {
        try { var b = entity.GetBoolean(key); if (b.HasValue) return b.Value; } catch { /* not a bool type */ }
        try { var s = entity.GetString(key); if (s != null) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase); } catch { /* ignore */ }
        return defaultValue;
    }
}
