using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlyerTracker.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FlyerTracker.Api.Functions;

public class SaveLocationFunction
{
    private const string PhotoContainer = "photos";
    private readonly TableServiceClient _tableService;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<SaveLocationFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimiter _rateLimiter;

    public SaveLocationFunction(TableServiceClient tableService, BlobServiceClient blobService,
        ILogger<SaveLocationFunction> logger, ApiKeyValidator apiKey, RateLimiter rateLimiter)
    {
        _tableService = tableService;
        _blobService = blobService;
        _logger = logger;
        _apiKey = apiKey;
        _rateLimiter = rateLimiter;
    }

    [Function("SaveLocation")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "save-location")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };

            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimiter.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            string? name, lostDog, timestamp;
            double? latitude, longitude, accuracy;
            IFormFile? photo = null;

            var contentType = req.ContentType ?? "";

            if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                // ── Multipart: form fields + optional photo ──
                var form = await req.ReadFormAsync();
                name = form["name"].FirstOrDefault();
                lostDog = form["lostDog"].FirstOrDefault();
                latitude = double.TryParse(form["latitude"], System.Globalization.CultureInfo.InvariantCulture, out var lat) ? lat : null;
                longitude = double.TryParse(form["longitude"], System.Globalization.CultureInfo.InvariantCulture, out var lon) ? lon : null;
                accuracy = double.TryParse(form["accuracy"], System.Globalization.CultureInfo.InvariantCulture, out var acc) ? acc : (double?)null;
                timestamp = form["timestamp"].FirstOrDefault();
                photo = form.Files.GetFile("photo");
            }
            else
            {
                // ── JSON body (backward compatible, no photo) ──
                var body = await JsonSerializer.DeserializeAsync<LocationRequest>(req.Body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                name = body?.Name;
                lostDog = body?.LostDog;
                latitude = body?.Latitude;
                longitude = body?.Longitude;
                accuracy = body?.Accuracy;
                timestamp = body?.Timestamp;
            }

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(lostDog) ||
                latitude is null || longitude is null)
            {
                return new BadRequestObjectResult(new { error = "Fehlende Pflichtfelder" });
            }

            // RowKey = reverse timestamp (newest records first in queries)
            var rowKey = (9_999_999_999_999 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .ToString("D15");

            // ── Upload photo to Blob Storage (if provided) ──
            string? photoUrl = null;
            if (photo is not null && photo.Length > 0)
            {
                // Limit to 5 MB
                if (photo.Length > 5 * 1024 * 1024)
                    return new BadRequestObjectResult(new { error = "Foto darf maximal 5 MB groß sein" });

                var container = _blobService.GetBlobContainerClient(PhotoContainer);
                await container.CreateIfNotExistsAsync(PublicAccessType.Blob);

                var ext = Path.GetExtension(photo.FileName)?.ToLowerInvariant();
                if (string.IsNullOrEmpty(ext) || !new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(ext))
                    ext = ".jpg";

                var blobName = $"{name}/{rowKey}{ext}";
                var blobClient = container.GetBlobClient(blobName);

                using var stream = photo.OpenReadStream();
                await blobClient.UploadAsync(stream, new BlobHttpHeaders
                {
                    ContentType = photo.ContentType ?? "image/jpeg"
                });

                photoUrl = blobClient.Uri.ToString();
            }

            var tableClient = _tableService.GetTableClient("GPSRecords");
            await tableClient.CreateIfNotExistsAsync();

            var entity = new TableEntity(name, rowKey)
            {
                { "LostDog", lostDog },
                { "Latitude", latitude.Value },
                { "Longitude", longitude.Value },
                { "Accuracy", accuracy ?? 0 },
                { "RecordedAt", timestamp ?? DateTime.UtcNow.ToString("o") }
            };

            if (photoUrl is not null)
                entity["PhotoUrl"] = photoUrl;

            await tableClient.AddEntityAsync(entity);

            _logger.LogInformation("Location saved: {Name} at {Lat},{Lon} ({LostDog}) Photo:{HasPhoto}",
                name, latitude, longitude, lostDog, photoUrl is not null);

            return new CreatedResult("", new { message = "Standort gespeichert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving location");
            return new StatusCodeResult(500);
        }
    }

    private record LocationRequest
    {
        public string? Name { get; init; }
        public string? LostDog { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
        public double? Accuracy { get; init; }
        public string? Timestamp { get; init; }
    }
}
