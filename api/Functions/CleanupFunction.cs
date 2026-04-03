using System.Net;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class CleanupFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<CleanupFunction> _logger;

    public CleanupFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, BlobServiceClient blobService, ILogger<CleanupFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _blobService = blobService; _logger = logger;
    }

    [Function("CleanupPreview")]
    public async Task<HttpResponseData> Preview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/cleanup/preview")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "maintenance.admin"); if (ctx is null) return pe!;
        var days = req.GetQueryInt("olderThanDays", 0);
        if (days < 1) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "olderThanDays muss mindestens 1 sein.");
        var lostDogFilter = req.GetQueryParam("lostDog");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "GPSRecords");
            int recordCount = 0, photoCount = 0;
            await foreach (var entity in table.QueryAsync<TableEntity>())
            {
                if (!IsOlderThan(entity, cutoff)) continue;
                if (!string.IsNullOrEmpty(lostDogFilter) && (entity.GetString("LostDog") ?? "") != lostDogFilter) continue;
                recordCount++;
                if (!string.IsNullOrEmpty(entity.GetString("PhotoUrl"))) photoCount++;
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { recordCount, photoCount, olderThanDays = days, cutoffDate = cutoff.ToString("yyyy-MM-dd"), lostDog = lostDogFilter ?? "" });
        }
        catch (Exception ex) { _logger.LogError(ex, "Cleanup preview failed"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("CleanupExecute")]
    public async Task<HttpResponseData> Execute(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/cleanup")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "maintenance.admin"); if (ctx is null) return pe!;
        var days = req.GetQueryInt("olderThanDays", 0);
        if (days < 1) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "olderThanDays muss mindestens 1 sein.");
        var lostDogFilter = req.GetQueryParam("lostDog");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "GPSRecords");
            var container = _blobService.GetBlobContainerClient(BlobHelper.PhotoContainer);
            var toDelete = new List<(string pk, string rk, string? photoUrl)>();
            await foreach (var entity in table.QueryAsync<TableEntity>())
            {
                if (!IsOlderThan(entity, cutoff)) continue;
                if (!string.IsNullOrEmpty(lostDogFilter) && (entity.GetString("LostDog") ?? "") != lostDogFilter) continue;
                toDelete.Add((entity.PartitionKey, entity.RowKey, entity.GetString("PhotoUrl")));
            }
            int deletedRecords = 0, deletedPhotos = 0;
            foreach (var (pk, rk, photoUrl) in toDelete)
            {
                if (!string.IsNullOrEmpty(photoUrl))
                {
                    var blobName = BlobHelper.ExtractBlobName(photoUrl);
                    if (blobName is not null) { try { await container.DeleteBlobIfExistsAsync(blobName); deletedPhotos++; } catch { } }
                }
                try { await table.DeleteEntityAsync(pk, rk); deletedRecords++; } catch { }
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { deletedRecords, deletedPhotos, olderThanDays = days, cutoffDate = cutoff.ToString("yyyy-MM-dd"), message = $"{deletedRecords} Einträge und {deletedPhotos} Fotos gelöscht." });
        }
        catch (Exception ex) { _logger.LogError(ex, "Cleanup failed"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private static bool IsOlderThan(TableEntity entity, DateTimeOffset cutoff)
    {
        var recordedAt = entity.GetString("RecordedAt");
        if (!string.IsNullOrEmpty(recordedAt) && DateTimeOffset.TryParse(recordedAt, out var dt)) return dt < cutoff;
        return entity.Timestamp.HasValue && entity.Timestamp.Value < cutoff;
    }
}
