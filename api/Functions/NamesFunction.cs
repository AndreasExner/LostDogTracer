using System.Text.Json;
using Azure.Data.Tables;
using FlyerTracker.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FlyerTracker.Api.Functions;

public class NamesFunction
{
    private readonly TableServiceClient _tableService;
    private readonly ILogger<NamesFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;
    private readonly RateLimitProvider _rateLimit;

    public NamesFunction(TableServiceClient tableService, ILogger<NamesFunction> logger,
        ApiKeyValidator apiKey, AdminAuth adminAuth, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
        _rateLimit = rateLimit;
    }

    [Function("GetNames")]
    public async Task<IActionResult> GetNames(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "names")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var tableClient = _tableService.GetTableClient("Names");
            await tableClient.CreateIfNotExistsAsync();

            var names = new List<string>();

            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                var name = entity.GetString("Name") ?? entity.RowKey;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }

            names.Sort(StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false));

            return new OkObjectResult(names);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading names");
            return new StatusCodeResult(500);
        }
    }

    [Function("GetNamesAdmin")]
    public async Task<IActionResult> GetNamesAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/names")] HttpRequest req)
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
            var tableClient = _tableService.GetTableClient("Names");
            await tableClient.CreateIfNotExistsAsync();

            var items = new List<object>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                items.Add(new
                {
                    partitionKey = entity.PartitionKey,
                    rowKey = entity.RowKey,
                    name = entity.GetString("Name") ?? entity.RowKey
                });
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(
                ((dynamic)a).name, ((dynamic)b).name));

            return new OkObjectResult(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading names (admin)");
            return new StatusCodeResult(500);
        }
    }

    [Function("CreateName")]
    public async Task<IActionResult> CreateName(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/names")] HttpRequest req)
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
            var name = body.GetProperty("name").GetString();

            if (string.IsNullOrWhiteSpace(name))
                return new BadRequestObjectResult(new { error = "Name darf nicht leer sein" });

            var tableClient = _tableService.GetTableClient("Names");
            await tableClient.CreateIfNotExistsAsync();

            var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
            var entity = new TableEntity("names", rowKey)
            {
                { "Name", name.Trim() }
            };

            await tableClient.AddEntityAsync(entity);
            _logger.LogInformation("Name created: {Name}", name);

            return new CreatedResult("", new { partitionKey = "names", rowKey, name = name.Trim() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating name");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteName")]
    public async Task<IActionResult> DeleteName(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/names/{rowKey}")] HttpRequest req,
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
            var tableClient = _tableService.GetTableClient("Names");
            await tableClient.DeleteEntityAsync("names", rowKey);
            _logger.LogInformation("Name deleted: RowKey={RowKey}", rowKey);
            return new OkObjectResult(new { message = "Gelöscht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting name");
            return new StatusCodeResult(500);
        }
    }
}
