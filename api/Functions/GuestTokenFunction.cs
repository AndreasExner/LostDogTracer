using System.Net;
using System.Security.Cryptography;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class GuestTokenFunction
{
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<GuestTokenFunction> _logger;

    public GuestTokenFunction(ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<GuestTokenFunction> logger)
    {
        _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _logger = logger;
    }

    [Function("GuestRegister")]
    public async Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "guest/register")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (body, be) = await req.ReadJsonBodyAsync<RegisterRequest>(); if (body is null) return be!;
        if (string.IsNullOrWhiteSpace(body.Uuid) || string.IsNullOrWhiteSpace(body.DogKey) || string.IsNullOrWhiteSpace(body.TenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId, uuid und dogKey sind erforderlich.");
        var uuid = body.Uuid.Trim();
        if (uuid.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(uuid, @"^[a-zA-Z0-9\-]+$"))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültige UUID.");
        try
        {
            var table = _tables.GetTableClient(body.TenantId, "GuestTokens");
            try
            {
                var existing = await table.GetEntityAsync<TableEntity>("guest", uuid);
                return req.CreateJsonResponse(HttpStatusCode.OK, new { token = existing.Value.GetString("Token"), existing = true });
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace("+", "-").Replace("/", "_").TrimEnd('=');
            var entity = new TableEntity("guest", uuid) { { "Token", token }, { "DogKey", body.DogKey.Trim() }, { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") } };
            if (!string.IsNullOrWhiteSpace(body.NickName)) entity["NickName"] = InputSanitizer.StripHtml(body.NickName.Trim());
            await table.AddEntityAsync(entity);
            return req.CreateJsonResponse(HttpStatusCode.Created, new { token, existing = false });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error in guest register"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("GuestUpdateNickname")]
    public async Task<HttpResponseData> UpdateNickname(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "guest/nickname")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (body, be) = await req.ReadJsonBodyAsync<NicknameRequest>(); if (body is null) return be!;
        if (string.IsNullOrWhiteSpace(body.Uuid) || string.IsNullOrWhiteSpace(body.TenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId und uuid sind erforderlich.");
        try
        {
            var table = _tables.GetTableClient(body.TenantId, "GuestTokens");
            var entity = (await table.GetEntityAsync<TableEntity>("guest", body.Uuid.Trim())).Value;
            entity["NickName"] = InputSanitizer.StripHtml((body.NickName ?? "").Trim());
            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return req.CreateErrorResponse(HttpStatusCode.NotFound, "Gast nicht gefunden."); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating guest nickname"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("GuestGetNickname")]
    public async Task<HttpResponseData> GetNickname(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "guest/nickname")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var token = req.GetQueryParam("token");
        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(tenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId und token sind erforderlich.");
        try
        {
            var table = _tables.GetTableClient(tenantId, "GuestTokens");
            await foreach (var entity in table.QueryAsync<TableEntity>(filter: "PartitionKey eq 'guest'", select: new[] { "Token", "NickName" }))
            {
                if (entity.GetString("Token") == token.Trim())
                    return req.CreateJsonResponse(HttpStatusCode.OK, new { nickName = entity.GetString("NickName") ?? "" });
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { nickName = "" });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error getting guest nickname"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private record RegisterRequest(string? TenantId, string? Uuid, string? DogKey, string? NickName);
    private record NicknameRequest(string? TenantId, string? Uuid, string? NickName);
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
