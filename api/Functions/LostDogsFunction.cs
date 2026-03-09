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

            var items = new List<(string display, string location, string suffix)>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                var location = entity.GetString("Location") ?? entity.RowKey;
                var suffix = entity.GetString("Suffix") ?? "";
                if (!string.IsNullOrWhiteSpace(location))
                {
                    var display = string.IsNullOrEmpty(suffix) ? location : $"{location} ({suffix})";
                    items.Add((display, location, suffix));
                }
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.display, b.display));

            return new OkObjectResult(items.Select(i => i.location));
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
                var location = entity.GetString("Location") ?? entity.RowKey;
                return new OkObjectResult(new { location });
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
            if (!_adminAuth.ValidateToken(req))
                return AdminAuth.Unauthorized();
            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.CreateIfNotExistsAsync();

            var items = new List<(string partitionKey, string rowKey, string location, string suffix)>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                items.Add((
                    entity.PartitionKey,
                    entity.RowKey,
                    entity.GetString("Location") ?? entity.RowKey,
                    entity.GetString("Suffix") ?? ""
                ));
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.location, b.location));

            return new OkObjectResult(items.Select(i => new { i.partitionKey, i.rowKey, i.location, i.suffix }));
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
            if (!_adminAuth.ValidateToken(req))
                return AdminAuth.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var location = body.GetProperty("location").GetString();

            if (string.IsNullOrWhiteSpace(location))
                return new BadRequestObjectResult(new { error = "Name darf nicht leer sein" });

            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.CreateIfNotExistsAsync();

            var trimmedLocation = location.Trim();

            // Generate cryptographically random 6-char alphanumeric suffix
            var suffix = GenerateRandomSuffix(6);
            var rowKey = $"{trimmedLocation}_{suffix}";

            var entity = new TableEntity("locations", rowKey)
            {
                { "Location", trimmedLocation },
                { "Suffix", suffix }
            };

            await tableClient.AddEntityAsync(entity);
            _logger.LogInformation("Lost dog created: {Location} ({Suffix})", trimmedLocation, suffix);

            return new CreatedResult("", new { partitionKey = "locations", rowKey, location = trimmedLocation, suffix });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating lost dog");
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
            if (!_adminAuth.ValidateToken(req))
                return AdminAuth.Unauthorized();
            var tableClient = _tableService.GetTableClient("LostDogs");
            await tableClient.DeleteEntityAsync("locations", rowKey);
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
