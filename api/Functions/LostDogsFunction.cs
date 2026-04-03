using System.Net;
using System.Security.Cryptography;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class LostDogsFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<LostDogsFunction> _logger;

    public LostDogsFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<LostDogsFunction> logger)
    {
        _auth = auth;
        _apiKey = apiKey;
        _rateLimiter = rateLimiter;
        _tables = tables;
        _logger = logger;
    }

    // Public: sorted list for dropdowns (tenantId from query)
    [Function("GetLostDogs")]
    public async Task<HttpResponseData> GetLostDogs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lost-dogs")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;

        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var (ctx, _) = req.ValidateToken(_auth);
            if (ctx is not null) tenantId = ctx.TenantId;
        }
        if (string.IsNullOrWhiteSpace(tenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");

        try
        {
            var table = _tables.GetTableClient(tenantId, "LostDogs");
            var items = new List<(string rowKey, string displayName)>();

            await foreach (var entity in table.QueryAsync<TableEntity>())
            {
                var displayName = entity.GetString("DisplayName") ?? entity.RowKey;
                if (!string.IsNullOrWhiteSpace(displayName))
                    items.Add((entity.RowKey, displayName));
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.displayName, b.displayName));
            return req.CreateJsonResponse(HttpStatusCode.OK, items.Select(i => new { rowKey = i.rowKey, displayName = i.displayName }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading lost dogs");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("GetLostDogByKey")]
    public async Task<HttpResponseData> GetLostDogByKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lost-dogs/by-key/{key}")] HttpRequestData req, string key)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;

        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");
        if (string.IsNullOrWhiteSpace(key) || key.Length != 6)
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Ungültiger Key.");

        try
        {
            var table = _tables.GetTableClient(tenantId, "LostDogs");
            var filter = $"Suffix eq '{key.Replace("'", "''")}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter))
            {
                return req.CreateJsonResponse(HttpStatusCode.OK, new
                {
                    displayName = entity.GetString("DisplayName") ?? entity.RowKey,
                    rowKey = entity.RowKey
                });
            }
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Hund nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up dog by key");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("GetLostDogByOwnerKey")]
    public async Task<HttpResponseData> GetLostDogByOwnerKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "lost-dogs/by-owner-key/{key}")] HttpRequestData req, string key)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;

        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");
        if (string.IsNullOrWhiteSpace(key) || key.Length < 16)
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Ungültiger Key.");

        try
        {
            var table = _tables.GetTableClient(tenantId, "LostDogs");
            var filter = $"OwnerKey eq '{key.Replace("'", "''")}'";
            await foreach (var entity in table.QueryAsync<TableEntity>(filter))
            {
                return req.CreateJsonResponse(HttpStatusCode.OK, new
                {
                    displayName = entity.GetString("DisplayName") ?? entity.RowKey,
                    rowKey = entity.RowKey
                });
            }
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Hund nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up dog by owner key");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("GetLostDogsAdmin")]
    public async Task<HttpResponseData> GetLostDogsAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/lost-dogs")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "dogs.read");
        if (ctx is null) return permError!;

        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "LostDogs");
            var items = new List<(string pk, string rk, string displayName, string suffix)>();
            await foreach (var entity in table.QueryAsync<TableEntity>())
            {
                items.Add((entity.PartitionKey, entity.RowKey,
                    entity.GetString("DisplayName") ?? entity.RowKey,
                    entity.GetString("Suffix") ?? ""));
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.displayName, b.displayName));
            return req.CreateJsonResponse(HttpStatusCode.OK,
                items.Select(i => new { partitionKey = i.pk, rowKey = i.rk, displayName = i.displayName, suffix = i.suffix }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading lost dogs (admin)");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("GenerateOwnerKey")]
    public async Task<HttpResponseData> GenerateOwnerKey(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/lost-dogs/{rowKey}/owner-key")] HttpRequestData req, string rowKey)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "dogs.owner");
        if (ctx is null) return permError!;

        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "LostDogs");
            var entityResponse = await table.GetEntityAsync<TableEntity>("lostdogs", rowKey);
            var entity = entityResponse.Value;

            var ownerKey = entity.GetString("OwnerKey");
            var force = string.Equals(req.GetQueryParam("force"), "true", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(ownerKey) || force)
            {
                ownerKey = GenerateRandomSuffix(24);
                entity["OwnerKey"] = ownerKey;
                await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { ownerKey });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Hund nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating owner key");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("CreateLostDog")]
    public async Task<HttpResponseData> CreateLostDog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/lost-dogs")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "dogs.write");
        if (ctx is null) return permError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<CreateDogRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.Name))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Name darf nicht leer sein.");

        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "LostDogs");
            var trimmedName = body.Name.Trim();
            var location = body.Location?.Trim() ?? "";
            var displayName = string.IsNullOrEmpty(location) ? trimmedName : $"{trimmedName}, {location}";
            var suffix = GenerateRandomSuffix(6);
            var rowKey = $"{trimmedName}_{suffix}";

            var entity = new TableEntity("lostdogs", rowKey)
            {
                { "DisplayName", displayName },
                { "Suffix", suffix }
            };
            await table.AddEntityAsync(entity);
            return req.CreateJsonResponse(HttpStatusCode.Created, new { partitionKey = "lostdogs", rowKey, displayName, suffix });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating lost dog");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("UpdateLostDog")]
    public async Task<HttpResponseData> UpdateLostDog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/lost-dogs/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "dogs.write");
        if (ctx is null) return permError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<UpdateDogRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.DisplayName))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Anzeigename darf nicht leer sein.");

        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "LostDogs");
            var entity = (await table.GetEntityAsync<TableEntity>("lostdogs", rowKey)).Value;
            entity["DisplayName"] = body.DisplayName.Trim();
            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Hund nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating lost dog");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("DeleteLostDog")]
    public async Task<HttpResponseData> DeleteLostDog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/lost-dogs/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "dogs.write");
        if (ctx is null) return permError!;

        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "LostDogs");
            await table.DeleteEntityAsync("lostdogs", rowKey);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Gelöscht." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Hund nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting lost dog");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    private static string GenerateRandomSuffix(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(chars, length);
    }

    private record CreateDogRequest(string? Name, string? Location);
    private record UpdateDogRequest(string? DisplayName);
}
