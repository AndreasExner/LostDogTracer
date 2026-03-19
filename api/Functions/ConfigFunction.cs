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
                imprintUrl = entity.GetString("ImprintUrl") ?? ""
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

    private async Task<TableEntity> GetOrSeedConfigAsync()
    {
        var table = _tableService.GetTableClient(TableName);
        await table.CreateIfNotExistsAsync();

        try
        {
            var response = await table.GetEntityAsync<TableEntity>(PK, RK);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Seed defaults
            var entity = new TableEntity(PK, RK)
            {
                { "SiteBanner", "Mein Org Name hier" },
                { "GuestCategoryRowKey", "001772623834586" },
                { "PrivacyUrl", "https://mein-impressum-hier.org" },
                { "ImprintUrl", "https://mein-impressum-hier.org" }
            };
            await table.AddEntityAsync(entity);
            return entity;
        }
    }
}
