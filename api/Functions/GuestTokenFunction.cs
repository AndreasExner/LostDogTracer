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
