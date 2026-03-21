using System.Security.Cryptography;
using System.Text.Json;
using Azure.Data.Tables;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class LostDogsFunction
{
    private readonly TableServiceClient _tableService;
    private readonly ILogger<LostDogsFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;
    private readonly RateLimitProvider _rateLimit;

    public LostDogsFunction(TableServiceClient tableService, ILogger<LostDogsFunction> logger,
        ApiKeyValidator apiKey, AdminAuth adminAuth, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
        _rateLimit = rateLimit;
    }

    [Function("GetLostDogs")]
    public async Task<IActionResult> GetLostDogs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lost-dogs")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.CreateIfNotExistsAsync();

            var items = new List<(string rowKey, string display, string displayName, string suffix)>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                var displayName = entity.GetString("DisplayName") ?? entity.GetString("Location") ?? entity.RowKey;
                var suffix = entity.GetString("Suffix") ?? "";
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    var display = string.IsNullOrEmpty(suffix) ? displayName : $"{displayName} ({suffix})";
                    items.Add((entity.RowKey, display, displayName, suffix));
                }
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.display, b.display));

            return new OkObjectResult(items.Select(i => new { rowKey = i.rowKey, displayName = i.displayName }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading lost dogs");
            return new StatusCodeResult(500);
        }
    }

    [Function("GetLostDogByKey")]
    public async Task<IActionResult> GetLostDogByKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lost-dogs/by-key/{key}")] HttpRequest req,
        string key)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            if (string.IsNullOrWhiteSpace(key) || key.Length != 6)
                return new NotFoundObjectResult(new { error = "Ungültiger Key" });

            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.CreateIfNotExistsAsync();

            var filter = $"Suffix eq '{key.Replace("'", "''")}'";
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter))
            {
                var displayName = entity.GetString("DisplayName") ?? entity.GetString("Location") ?? entity.RowKey;
                return new OkObjectResult(new { displayName, rowKey = entity.RowKey });
            }

            return new NotFoundObjectResult(new { error = "Hund nicht gefunden" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up lost dog by key {Key}", key);
            return new StatusCodeResult(500);
        }
    }

    [Function("GetLostDogsAdmin")]
    public async Task<IActionResult> GetLostDogsAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/lost-dogs")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 3) == 0)
                return AdminAuth.Forbidden();
            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.CreateIfNotExistsAsync();

            var items = new List<(string partitionKey, string rowKey, string displayName, string suffix)>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                items.Add((
                    entity.PartitionKey,
                    entity.RowKey,
                    entity.GetString("DisplayName") ?? entity.GetString("Location") ?? entity.RowKey,
                    entity.GetString("Suffix") ?? ""
                ));
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.displayName, b.displayName));

            return new OkObjectResult(items.Select(i => new { i.partitionKey, i.rowKey, i.displayName, i.suffix }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading lost dogs (admin)");
            return new StatusCodeResult(500);
        }
    }

    [Function("CreateLostDog")]
    public async Task<IActionResult> CreateLostDog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/lost-dogs")] HttpRequest req)
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
            var name = body.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var location = body.TryGetProperty("location", out var locProp) ? locProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(name))
                return new BadRequestObjectResult(new { error = "Name darf nicht leer sein" });

            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.CreateIfNotExistsAsync();

            var trimmedName = name.Trim();
            var trimmedLocation = location?.Trim() ?? "";
            var displayName = string.IsNullOrEmpty(trimmedLocation) ? trimmedName : $"{trimmedName}, {trimmedLocation}";

            // Generate cryptographically random 6-char alphanumeric suffix
            var suffix = GenerateRandomSuffix(6);
            var rowKey = $"{trimmedName}_{suffix}";

            var entity = new TableEntity("lostdogs", rowKey)
            {
                { "DisplayName", displayName },
                { "Suffix", suffix }
            };

            await tableClient.AddEntityAsync(entity);
            _logger.LogInformation("Lost dog created: {DisplayName} ({Suffix})", displayName, suffix);

            return new CreatedResult("", new { partitionKey = "lostdogs", rowKey, displayName, suffix });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating lost dog");
            return new StatusCodeResult(500);
        }
    }

    [Function("UpdateLostDog")]
    public async Task<IActionResult> UpdateLostDog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/lost-dogs/{rowKey}")] HttpRequest req,
        string rowKey)
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
            var displayName = body.TryGetProperty("displayName", out var dnProp) ? dnProp.GetString() : null;

            if (string.IsNullOrWhiteSpace(displayName))
                return new BadRequestObjectResult(new { error = "Anzeigename darf nicht leer sein" });

            var tableClient = _tableService.GetTableClient("LostDogs");
            var entity = await tableClient.GetEntityAsync<TableEntity>("lostdogs", rowKey);
            entity.Value["DisplayName"] = displayName.Trim();
            await tableClient.UpdateEntityAsync(entity.Value, entity.Value.ETag, TableUpdateMode.Replace);

            _logger.LogInformation("Lost dog updated: RowKey={RowKey}", rowKey);
            return new OkObjectResult(new { message = "Aktualisiert" });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return new NotFoundObjectResult(new { error = "Hund nicht gefunden" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating lost dog");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteLostDog")]
    public async Task<IActionResult> DeleteLostDog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/lost-dogs/{rowKey}")] HttpRequest req,
        string rowKey)
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
            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.DeleteEntityAsync("lostdogs", rowKey);
            _logger.LogInformation("Lost dog deleted: RowKey={RowKey}", rowKey);
            return new OkObjectResult(new { message = "Gelöscht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting lost dog");
            return new StatusCodeResult(500);
        }
    }

    private static string GenerateRandomSuffix(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(chars, length);
    }
}
