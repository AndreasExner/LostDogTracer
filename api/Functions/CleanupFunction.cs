using Azure.Data.Tables;
using Azure.Storage.Blobs;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class CleanupFunction
{
    private const string PhotoContainer = "photos";
    private readonly TableServiceClient _tableService;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<CleanupFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;
    private readonly RateLimitProvider _rateLimit;

    public CleanupFunction(TableServiceClient tableService, BlobServiceClient blobService,
        ILogger<CleanupFunction> logger, ApiKeyValidator apiKey,
        AdminAuth adminAuth, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _blobService = blobService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
        _rateLimit = rateLimit;
    }

    /// <summary>Preview: count records + photos older than X days, optionally filtered by dog.</summary>
    [Function("CleanupPreview")]
    public async Task<IActionResult> Preview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/cleanup/preview")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var daysStr = req.Query["olderThanDays"].FirstOrDefault();
            if (!int.TryParse(daysStr, out var days) || days < 1)
                return new BadRequestObjectResult(new { error = "olderThanDays muss mindestens 1 sein" });

            var lostDogFilter = req.Query["lostDog"].FirstOrDefault();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

            var tableClient = _tableService.GetTableClient("GPSRecords");
            await tableClient.CreateIfNotExistsAsync();

            int recordCount = 0;
            int photoCount = 0;

            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                if (!IsOlderThan(entity, cutoff)) continue;
                if (!string.IsNullOrEmpty(lostDogFilter))
                {
                    var dog = entity.GetString("LostDog") ?? "";
                    if (dog != lostDogFilter) continue;
                }

                recordCount++;
                if (!string.IsNullOrEmpty(entity.GetString("PhotoUrl")))
                    photoCount++;
            }

            return new OkObjectResult(new
            {
                recordCount,
                photoCount,
                olderThanDays = days,
                cutoffDate = cutoff.ToString("yyyy-MM-dd"),
                lostDog = lostDogFilter ?? ""
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup preview failed");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Execute: delete records + photos older than X days, optionally filtered by dog.</summary>
    [Function("CleanupExecute")]
    public async Task<IActionResult> Execute(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/cleanup")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var daysStr = req.Query["olderThanDays"].FirstOrDefault();
            if (!int.TryParse(daysStr, out var days) || days < 1)
                return new BadRequestObjectResult(new { error = "olderThanDays muss mindestens 1 sein" });

            var lostDogFilter = req.Query["lostDog"].FirstOrDefault();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

            var tableClient = _tableService.GetTableClient("GPSRecords");
            await tableClient.CreateIfNotExistsAsync();
            var container = _blobService.GetBlobContainerClient(PhotoContainer);

            // Collect records to delete
            var toDelete = new List<(string pk, string rk, string? photoUrl)>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                if (!IsOlderThan(entity, cutoff)) continue;
                if (!string.IsNullOrEmpty(lostDogFilter))
                {
                    var dog = entity.GetString("LostDog") ?? "";
                    if (dog != lostDogFilter) continue;
                }
                toDelete.Add((entity.PartitionKey, entity.RowKey, entity.GetString("PhotoUrl")));
            }

            int deletedRecords = 0;
            int deletedPhotos = 0;

            foreach (var (pk, rk, photoUrl) in toDelete)
            {
                // Delete photo blob
                if (!string.IsNullOrEmpty(photoUrl))
                {
                    try
                    {
                        var uri = new Uri(photoUrl);
                        var blobName = string.Join("/", uri.Segments.Skip(2)).TrimStart('/');
                        if (!string.IsNullOrEmpty(blobName))
                        {
                            await container.DeleteBlobIfExistsAsync(blobName);
                            deletedPhotos++;
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete photo blob during cleanup"); }
                }

                // Delete table record
                try
                {
                    await tableClient.DeleteEntityAsync(pk, rk);
                    deletedRecords++;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete record {PK}/{RK} during cleanup", pk, rk); }
            }

            _logger.LogInformation("Cleanup completed: {Records} records, {Photos} photos deleted (older than {Days} days)",
                deletedRecords, deletedPhotos, days);

            return new OkObjectResult(new
            {
                deletedRecords,
                deletedPhotos,
                olderThanDays = days,
                cutoffDate = cutoff.ToString("yyyy-MM-dd"),
                message = $"{deletedRecords} Einträge und {deletedPhotos} Fotos gelöscht"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cleanup execution failed");
            return new StatusCodeResult(500);
        }
    }

    private static bool IsOlderThan(TableEntity entity, DateTimeOffset cutoff)
    {
        // Try RecordedAt first (ISO string), then fall back to Timestamp
        var recordedAt = entity.GetString("RecordedAt");
        if (!string.IsNullOrEmpty(recordedAt) && DateTimeOffset.TryParse(recordedAt, out var dt))
            return dt < cutoff;
        return entity.Timestamp.HasValue && entity.Timestamp.Value < cutoff;
    }
}
