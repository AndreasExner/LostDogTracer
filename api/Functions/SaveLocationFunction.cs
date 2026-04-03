using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class SaveLocationFunction
{
    private const int MaxCommentLength = 40;
    private const long ReverseTimestampBase = 9_999_999_999_999;

    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<SaveLocationFunction> _logger;

    public SaveLocationFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, BlobServiceClient blobService, ILogger<SaveLocationFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _blobService = blobService; _logger = logger;
    }

    [Function("SaveLocation")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "save-location")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        try
        {
            string? tenantId, name, lostDog, timestamp, comment, category, guestToken, ownerKey;
            double? latitude, longitude, accuracy;
            byte[]? photoBytes = null; string? photoFileName = null; string? photoContentType = null;

            var contentType = "";
            if (req.Headers.TryGetValues("Content-Type", out var ctValues)) contentType = ctValues.FirstOrDefault() ?? "";

            if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(contentType).Boundary).Value;
                if (string.IsNullOrEmpty(boundary)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültiger Content-Type.");

                var fields = new Dictionary<string, string>();
                var reader = new MultipartReader(boundary, req.Body);
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync()) != null)
                {
                    var disposition = section.GetContentDispositionHeader();
                    if (disposition == null) continue;
                    if (disposition.IsFileDisposition())
                    {
                        using var ms = new MemoryStream();
                        await section.Body.CopyToAsync(ms);
                        if (ms.Length > 0 && ms.Length <= BlobHelper.MaxPhotoSize)
                        {
                            photoBytes = ms.ToArray();
                            photoFileName = disposition.FileName.Value ?? "photo.jpg";
                            photoContentType = section.ContentType ?? "image/jpeg";
                        }
                    }
                    else if (disposition.IsFormDisposition())
                    {
                        var fieldName = disposition.Name.Value ?? "";
                        using var sr = new StreamReader(section.Body);
                        fields[fieldName] = await sr.ReadToEndAsync();
                    }
                }
                tenantId = fields.GetValueOrDefault("tenantId");
                name = fields.GetValueOrDefault("name");
                lostDog = fields.GetValueOrDefault("lostDog");
                latitude = double.TryParse(fields.GetValueOrDefault("latitude"), System.Globalization.CultureInfo.InvariantCulture, out var lat) ? lat : null;
                longitude = double.TryParse(fields.GetValueOrDefault("longitude"), System.Globalization.CultureInfo.InvariantCulture, out var lon) ? lon : null;
                accuracy = double.TryParse(fields.GetValueOrDefault("accuracy"), System.Globalization.CultureInfo.InvariantCulture, out var acc) ? acc : null;
                timestamp = fields.GetValueOrDefault("timestamp");
                comment = fields.GetValueOrDefault("comment");
                category = fields.GetValueOrDefault("category");
                guestToken = fields.GetValueOrDefault("guestToken");
                ownerKey = fields.GetValueOrDefault("ownerKey");
            }
            else
            {
                var body = await JsonSerializer.DeserializeAsync<LocationRequest>(req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                tenantId = body?.TenantId; name = body?.Name; lostDog = body?.LostDog;
                latitude = body?.Latitude; longitude = body?.Longitude; accuracy = body?.Accuracy;
                timestamp = body?.Timestamp; comment = body?.Comment; category = body?.Category;
                guestToken = body?.GuestToken; ownerKey = body?.OwnerKey;
            }

            // Try to get tenantId from token if not in body
            if (string.IsNullOrWhiteSpace(tenantId)) { var (c, _) = req.ValidateToken(_auth); if (c is not null) tenantId = c.TenantId; }
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(lostDog) || latitude is null || longitude is null)
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Fehlende Pflichtfelder (tenantId, name, lostDog, latitude, longitude).");

            var rowKey = (ReverseTimestampBase - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToString("D15");

            // Upload photo
            string? photoUrl = null;
            if (photoBytes is not null)
            {
                var ext = Path.GetExtension(photoFileName ?? ".jpg")?.ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || !BlobHelper.AllowedExtensions.Contains(ext)) ext = ".jpg";
                var container = _blobService.GetBlobContainerClient(BlobHelper.PhotoContainer);
                await container.CreateIfNotExistsAsync();
                var blobPath = BlobHelper.GetPhotoPath(tenantId, name, rowKey, ext);
                var blobClient = container.GetBlobClient(blobPath);
                using var ms = new MemoryStream(photoBytes);
                await blobClient.UploadAsync(ms, new BlobHttpHeaders { ContentType = photoContentType ?? "image/jpeg" });
                photoUrl = blobClient.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddYears(5)).ToString();
            }

            var tableClient = _tables.GetTableClient(tenantId, "GPSRecords");
            if (comment is not null) { comment = InputSanitizer.StripHtml(comment); if (comment.Length > MaxCommentLength) comment = comment[..MaxCommentLength]; }

            var entity = new TableEntity(name, rowKey)
            {
                { "LostDog", lostDog }, { "Latitude", latitude.Value }, { "Longitude", longitude.Value },
                { "Accuracy", accuracy ?? 0 }, { "RecordedAt", timestamp ?? DateTime.UtcNow.ToString("o") }
            };
            if (photoUrl is not null) entity["PhotoUrl"] = photoUrl;
            if (!string.IsNullOrWhiteSpace(comment)) entity["Comment"] = comment.Trim();
            if (!string.IsNullOrWhiteSpace(category)) entity["Category"] = category.Trim();
            if (!string.IsNullOrWhiteSpace(guestToken)) entity["GuestToken"] = guestToken.Trim();
            if (!string.IsNullOrWhiteSpace(ownerKey)) entity["OwnerKey"] = ownerKey.Trim();

            await tableClient.AddEntityAsync(entity);
            _logger.LogInformation("Location saved: {Name} ({Dog}) Photo:{HasPhoto}", name, lostDog, photoUrl is not null);
            return req.CreateJsonResponse(HttpStatusCode.Created, new { message = "Standort gespeichert." });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error saving location"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private record LocationRequest(string? TenantId, string? Name, string? LostDog, double? Latitude, double? Longitude, double? Accuracy, string? Timestamp, string? Comment, string? Category, string? GuestToken, string? OwnerKey);
}
