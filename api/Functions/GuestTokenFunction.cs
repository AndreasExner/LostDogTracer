using System.Security.Cryptography;
using System.Text.Json;
using Azure.Data.Tables;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class GuestTokenFunction
{
    private const string TableName = "GuestTokens";
    private const string PK = "guest";

    private readonly TableServiceClient _tableService;
    private readonly ILogger<GuestTokenFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimit;

    public GuestTokenFunction(TableServiceClient tableService, ILogger<GuestTokenFunction> logger,
        ApiKeyValidator apiKey, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _logger = logger;
        _apiKey = apiKey;
        _rateLimit = rateLimit;
    }

    /// <summary>Register a guest: given a UUID, return an existing or new token.</summary>
    [Function("GuestRegister")]
    public async Task<IActionResult> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "guest/register")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var body = await JsonSerializer.DeserializeAsync<RegisterRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.Uuid) || string.IsNullOrWhiteSpace(body.DogKey))
                return new BadRequestObjectResult(new { error = "uuid und dogKey sind erforderlich" });

            // Sanitise UUID (max 64 chars, alphanumeric + hyphens only)
            var uuid = body.Uuid.Trim();
            if (uuid.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(uuid, @"^[a-zA-Z0-9\-]+$"))
                return new BadRequestObjectResult(new { error = "Ungültige UUID" });

            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            // Check if this UUID already has a token
            try
            {
                var existing = await table.GetEntityAsync<TableEntity>(PK, uuid);
                return new OkObjectResult(new
                {
                    token = existing.Value.GetString("Token"),
                    existing = true
                });
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                // Not found — create new token
            }

            var token = GenerateToken();
            var entity = new TableEntity(PK, uuid)
            {
                { "Token", token },
                { "DogKey", body.DogKey.Trim() },
                { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
            };

            if (!string.IsNullOrWhiteSpace(body.NickName))
                entity["NickName"] = InputSanitizer.StripHtml(body.NickName.Trim());

            await table.AddEntityAsync(entity);
            _logger.LogInformation("Guest registered: UUID={Uuid}", uuid);

            return new CreatedResult("", new { token, existing = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in guest register");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Update nickname for an existing guest token.</summary>
    [Function("GuestUpdateNickname")]
    public async Task<IActionResult> UpdateNickname(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "guest/nickname")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ung\u00fcltiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var body = await JsonSerializer.DeserializeAsync<NicknameRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.Uuid))
                return new BadRequestObjectResult(new { error = "uuid ist erforderlich" });

            var table = _tableService.GetTableClient(TableName);
            var response = await table.GetEntityAsync<TableEntity>(PK, body.Uuid.Trim());
            var entity = response.Value;

            entity["NickName"] = InputSanitizer.StripHtml((body.NickName ?? "").Trim());
            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);

            return new OkObjectResult(new { message = "Aktualisiert" });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return new NotFoundObjectResult(new { error = "Gast nicht gefunden" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating guest nickname");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Get nickname for a guest token.</summary>
    [Function("GuestGetNickname")]
    public async Task<IActionResult> GetNickname(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "guest/nickname")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var token = req.Query["token"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
                return new BadRequestObjectResult(new { error = "token ist erforderlich" });

            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            await foreach (var entity in table.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{PK}'",
                select: new[] { "Token", "NickName" }))
            {
                if (entity.GetString("Token") == token.Trim())
                {
                    return new OkObjectResult(new
                    {
                        nickName = entity.GetString("NickName") ?? ""
                    });
                }
            }

            return new OkObjectResult(new { nickName = "" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting guest nickname");
            return new StatusCodeResult(500);
        }
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private record RegisterRequest
    {
        public string? Uuid { get; init; }
        public string? DogKey { get; init; }
        public string? NickName { get; init; }
    }

    private record NicknameRequest
    {
        public string? Uuid { get; init; }
        public string? NickName { get; init; }
    }
}
