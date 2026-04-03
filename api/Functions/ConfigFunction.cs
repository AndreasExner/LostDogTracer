using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class ConfigFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<ConfigFunction> _logger;

    public ConfigFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<ConfigFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _logger = logger;
    }

    private static readonly Dictionary<string, string> DefaultValues = new()
    {
        ["SiteBanner"] = "Mein Org Name hier",
        ["GuestCategoryRowKey"] = "",
        ["PrivacyUrl"] = "docs/datenschutz.html",
        ["ImprintUrl"] = "docs/impressum.html",
        ["Doc1Label"] = "Einrichtung und erste Schritte", ["Doc1Link"] = "",
        ["Doc2Label"] = "Benutzer Handbuch", ["Doc2Link"] = "",
        ["Doc3Label"] = "Admin Handbuch", ["Doc3Link"] = "",
        ["DebugLogin"] = "false",
        ["FeatDeployment"] = "true",
        ["FeatEquipment"] = "true"
    };

    [Function("GetConfig")]
    public async Task<HttpResponseData> GetConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");
        try
        {
            var entity = await GetOrSeedConfigAsync(tenantId);
            return req.CreateJsonResponse(HttpStatusCode.OK, new
            {
                siteBanner = entity.GetString("SiteBanner") ?? "LostDogTracer",
                guestCategoryRowKey = entity.GetString("GuestCategoryRowKey") ?? "",
                privacyUrl = entity.GetString("PrivacyUrl") ?? "",
                imprintUrl = entity.GetString("ImprintUrl") ?? "",
                doc1Label = entity.GetString("Doc1Label") ?? "", doc1Link = entity.GetString("Doc1Link") ?? "",
                doc2Label = entity.GetString("Doc2Label") ?? "", doc2Link = entity.GetString("Doc2Link") ?? "",
                doc3Label = entity.GetString("Doc3Label") ?? "", doc3Link = entity.GetString("Doc3Link") ?? "",
                debugLogin = string.Equals(entity.GetString("DebugLogin") ?? "", "true", StringComparison.OrdinalIgnoreCase),
                featDeployment = GetBoolSafe(entity, "FeatDeployment", true),
                featEquipment = GetBoolSafe(entity, "FeatEquipment", true)
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading config"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("UpdateConfig")]
    public async Task<HttpResponseData> UpdateConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/config")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "config.admin"); if (ctx is null) return pe!;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var entity = await GetOrSeedConfigAsync(ctx.TenantId);
            var table = _tables.GetTableClient(ctx.TenantId, "Config");

            if (body.TryGetProperty("siteBanner", out var p)) entity["SiteBanner"] = p.GetString()?.Trim() ?? "";
            if (body.TryGetProperty("guestCategoryRowKey", out p)) entity["GuestCategoryRowKey"] = p.GetString()?.Trim() ?? "";
            if (body.TryGetProperty("privacyUrl", out p)) entity["PrivacyUrl"] = p.GetString()?.Trim() ?? "";
            if (body.TryGetProperty("imprintUrl", out p)) entity["ImprintUrl"] = p.GetString()?.Trim() ?? "";
            foreach (var f in new[] { "Doc1Label", "Doc1Link", "Doc2Label", "Doc2Link", "Doc3Label", "Doc3Link" })
            {
                var camel = char.ToLowerInvariant(f[0]) + f[1..];
                if (body.TryGetProperty(camel, out var dp)) entity[f] = dp.GetString()?.Trim() ?? "";
            }
            if (body.TryGetProperty("featDeployment", out p)) entity["FeatDeployment"] = p.GetBoolean();
            if (body.TryGetProperty("featEquipment", out p)) entity["FeatEquipment"] = p.GetBoolean();

            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Konfiguration gespeichert." });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error updating config"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private async Task<TableEntity> GetOrSeedConfigAsync(string tenantId)
    {
        var table = _tables.GetTableClient(tenantId, "Config");
        try
        {
            var resp = await table.GetEntityAsync<TableEntity>("config", "settings");
            var entity = resp.Value;
            bool updated = false;
            foreach (var (key, def) in DefaultValues)
            {
                if (!entity.ContainsKey(key) || string.IsNullOrEmpty(entity.GetString(key)))
                { entity[key] = def; updated = true; }
            }
            if (updated) await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
            return entity;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            var entity = new TableEntity("config", "settings");
            foreach (var (key, val) in DefaultValues) entity[key] = val;
            await table.AddEntityAsync(entity);
            return entity;
        }
    }

    private static bool GetBoolSafe(TableEntity entity, string key, bool defaultValue)
    {
        try { var b = entity.GetBoolean(key); if (b.HasValue) return b.Value; } catch { }
        try { var s = entity.GetString(key); if (s != null) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase); } catch { }
        return defaultValue;
    }
}
