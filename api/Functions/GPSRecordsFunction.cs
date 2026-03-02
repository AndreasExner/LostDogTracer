using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using FlyerTracker.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FlyerTracker.Api.Functions;

public class GPSRecordsFunction
{
    private const string PhotoContainer = "photos";
    private readonly TableServiceClient _tableService;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<GPSRecordsFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;

    public GPSRecordsFunction(TableServiceClient tableService, BlobServiceClient blobService,
        ILogger<GPSRecordsFunction> logger, ApiKeyValidator apiKey, AdminAuth adminAuth)
    {
        _tableService = tableService;
        _blobService = blobService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
    }

    [Function("GetGPSRecords")]
    public async Task<IActionResult> GetGPSRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/gps-records")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            if (!_adminAuth.ValidateToken(req))
                return AdminAuth.Unauthorized();
            var tableClient = _tableService.GetTableClient("GPSRecords");
            await tableClient.CreateIfNotExistsAsync();

            var lostDogFilter = req.Query["lostDog"].FirstOrDefault();
            var pageSizeStr = req.Query["pageSize"].FirstOrDefault();
            var pageStr = req.Query["page"].FirstOrDefault();

            int? pageSize = pageSizeStr == "all" ? null : int.TryParse(pageSizeStr, out var ps) ? ps : 20;
            int page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;

            var allRecords = new List<object>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                var lostDog = entity.GetString("LostDog") ?? "";
                if (!string.IsNullOrEmpty(lostDogFilter) && lostDog != lostDogFilter)
                    continue;

                allRecords.Add(new
                {
                    partitionKey = entity.PartitionKey,
                    rowKey = entity.RowKey,
                    name = entity.PartitionKey,
                    lostDog,
                    latitude = entity.GetDouble("Latitude") ?? 0,
                    longitude = entity.GetDouble("Longitude") ?? 0,
                    accuracy = entity.GetDouble("Accuracy") ?? 0,
                    recordedAt = entity.GetString("RecordedAt") ?? entity.Timestamp?.ToString("o") ?? "",
                    photoUrl = entity.GetString("PhotoUrl") ?? ""
                });
            }

            int totalCount = allRecords.Count;

            // Get unique lost dogs for filter dropdown
            var lostDogs = allRecords.Select(r => ((dynamic)r).lostDog as string)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .OrderBy(d => d, StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false))
                .ToList();

            // Paginate
            IEnumerable<object> pagedRecords;
            if (pageSize.HasValue)
            {
                pagedRecords = allRecords.Skip((page - 1) * pageSize.Value).Take(pageSize.Value);
            }
            else
            {
                pagedRecords = allRecords;
            }

            return new OkObjectResult(new
            {
                records = pagedRecords,
                totalCount,
                page,
                pageSize = pageSize ?? totalCount,
                totalPages = pageSize.HasValue ? (int)Math.Ceiling((double)totalCount / pageSize.Value) : 1,
                lostDogs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading GPS records");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteGPSRecords")]
    public async Task<IActionResult> DeleteGPSRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/gps-records/delete")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            if (!_adminAuth.ValidateToken(req))
                return AdminAuth.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<List<DeleteKey>>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || body.Count == 0)
                return new BadRequestObjectResult(new { error = "Keine Einträge zum Löschen" });

            var tableClient = _tableService.GetTableClient("GPSRecords");
            var container = _blobService.GetBlobContainerClient(PhotoContainer);
            int deleted = 0;

            foreach (var key in body)
            {
                try
                {
                    // Try to read entity first to get PhotoUrl
                    try
                    {
                        var entity = await tableClient.GetEntityAsync<TableEntity>(key.PartitionKey, key.RowKey);
                        var photoUrl = entity.Value.GetString("PhotoUrl");
                        if (!string.IsNullOrEmpty(photoUrl))
                        {
                            // Extract blob name from URL: {container}/{name}/{rowKey}.ext
                            var uri = new Uri(photoUrl);
                            var blobName = string.Join("/", uri.Segments.Skip(2)).TrimStart('/');
                            try { await container.DeleteBlobIfExistsAsync(blobName); }
                            catch { /* best effort */ }
                        }
                    }
                    catch { /* entity may already be gone */ }

                    await tableClient.DeleteEntityAsync(key.PartitionKey, key.RowKey);
                    deleted++;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("GPS record not found: {PK}/{RK}", key.PartitionKey, key.RowKey);
                }
            }

            _logger.LogInformation("Deleted {Count} GPS records", deleted);
            return new OkObjectResult(new { deleted, message = $"{deleted} Einträge gelöscht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting GPS records");
            return new StatusCodeResult(500);
        }
    }

    private record DeleteKey
    {
        public string PartitionKey { get; init; } = "";
        public string RowKey { get; init; } = "";
    }
}
