using System.Text.Json;
using Azure.Data.Tables;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class EquipmentFunction
{
    private const string TableName = "Equipment";
    private const string PK = "equipment";

    private readonly TableServiceClient _tableService;
    private readonly ILogger<EquipmentFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;
    private readonly RateLimitProvider _rateLimit;

    public EquipmentFunction(TableServiceClient tableService, ILogger<EquipmentFunction> logger,
        ApiKeyValidator apiKey, AdminAuth adminAuth, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
        _rateLimit = rateLimit;
    }

    [Function("GetEquipment")]
    public async Task<IActionResult> GetEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/equipment")] HttpRequest req)
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

            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            var items = new List<object>();
            await foreach (var entity in table.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{PK}'"))
            {
                items.Add(new
                {
                    rowKey = entity.RowKey,
                    displayName = entity.GetString("DisplayName") ?? "",
                    location = entity.GetString("Location") ?? "",
                    latitude = entity.GetDouble("Latitude"),
                    longitude = entity.GetDouble("Longitude")
                });
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(
                ((dynamic)a).displayName as string ?? "",
                ((dynamic)b).displayName as string ?? ""));

            return new OkObjectResult(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading equipment");
            return new StatusCodeResult(500);
        }
    }

    [Function("CreateEquipment")]
    public async Task<IActionResult> CreateEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/equipment")] HttpRequest req)
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

            var body = await JsonSerializer.DeserializeAsync<EquipmentRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.DisplayName))
                return new BadRequestObjectResult(new { error = "Bezeichnung erforderlich" });

            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
            var entity = new TableEntity(PK, rowKey)
            {
                { "DisplayName", body.DisplayName.Trim() }
            };

            if (!string.IsNullOrWhiteSpace(body.Location))
                entity["Location"] = body.Location.Trim();
            if (body.Latitude.HasValue)
                entity["Latitude"] = body.Latitude.Value;
            if (body.Longitude.HasValue)
                entity["Longitude"] = body.Longitude.Value;

            await table.AddEntityAsync(entity);
            _logger.LogInformation("Equipment created: {Name}", body.DisplayName);

            return new CreatedResult("", new { rowKey, displayName = body.DisplayName.Trim() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating equipment");
            return new StatusCodeResult(500);
        }
    }

    [Function("UpdateEquipment")]
    public async Task<IActionResult> UpdateEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/equipment/{rowKey}")] HttpRequest req,
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

            var body = await JsonSerializer.DeserializeAsync<EquipmentRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null)
                return new BadRequestObjectResult(new { error = "Daten erforderlich" });

            var table = _tableService.GetTableClient(TableName);
            var response = await table.GetEntityAsync<TableEntity>(PK, rowKey);
            var entity = response.Value;

            if (!string.IsNullOrWhiteSpace(body.DisplayName))
                entity["DisplayName"] = body.DisplayName.Trim();
            if (body.Location is not null)
                entity["Location"] = body.Location.Trim();
            if (body.Latitude.HasValue)
                entity["Latitude"] = body.Latitude.Value;
            if (body.Longitude.HasValue)
                entity["Longitude"] = body.Longitude.Value;

            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            _logger.LogInformation("Equipment updated: {RowKey}", rowKey);

            return new OkObjectResult(new { message = "Aktualisiert" });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return new NotFoundObjectResult(new { error = "Equipment nicht gefunden" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating equipment");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteEquipment")]
    public async Task<IActionResult> DeleteEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/equipment/{rowKey}")] HttpRequest req,
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

            var table = _tableService.GetTableClient(TableName);
            await table.DeleteEntityAsync(PK, rowKey);
            _logger.LogInformation("Equipment deleted: {RowKey}", rowKey);

            return new OkObjectResult(new { message = "Gelöscht" });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return new NotFoundObjectResult(new { error = "Equipment nicht gefunden" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting equipment");
            return new StatusCodeResult(500);
        }
    }

    private record EquipmentRequest
    {
        public string? DisplayName { get; init; }
        public string? Location { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
    }
}
