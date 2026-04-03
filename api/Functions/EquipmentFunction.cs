using System.Net;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class EquipmentFunction
{
    private const string PK = "equipment";
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<EquipmentFunction> _logger;

    public EquipmentFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<EquipmentFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _logger = logger;
    }

    [Function("GetEquipment")]
    public async Task<HttpResponseData> GetEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/equipment")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "equipment.read"); if (ctx is null) return pe!;
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Equipment");
            var items = new List<object>();
            await foreach (var ent in table.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{PK}'"))
                items.Add(new { rowKey = ent.RowKey, displayName = ent.GetString("DisplayName") ?? "", comment = ent.GetString("Comment") ?? "", userName = ent.GetString("UserName") ?? "", location = ent.GetString("Location") ?? "", latitude = ent.GetDouble("Latitude"), longitude = ent.GetDouble("Longitude") });
            return req.CreateJsonResponse(HttpStatusCode.OK, items);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading equipment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("GetEquipmentMembers")]
    public async Task<HttpResponseData> GetEquipmentMembers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/equipment/members")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "equipment.read"); if (ctx is null) return pe!;
        try
        {
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            var members = new List<object>();
            await foreach (var ent in usersTable.QueryAsync<TableEntity>(filter: "PartitionKey eq 'users'", select: new[] { "RowKey", "DisplayName", "Location", "Latitude", "Longitude" }))
            {
                var loc = ent.GetString("Location"); var lat = ent.GetDouble("Latitude"); var lng = ent.GetDouble("Longitude");
                if (!string.IsNullOrWhiteSpace(loc) && lat.HasValue && lng.HasValue)
                    members.Add(new { displayName = ent.GetString("DisplayName") ?? ent.RowKey, location = loc, latitude = lat.Value, longitude = lng.Value });
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, members);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading equipment members"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("CreateEquipment")]
    public async Task<HttpResponseData> CreateEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/equipment")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "equipment.write"); if (ctx is null) return pe!;
        var (body, be) = await req.ReadJsonBodyAsync<EquipmentRequest>(); if (body is null) return be!;
        if (string.IsNullOrWhiteSpace(body.DisplayName)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Bezeichnung erforderlich.");
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Equipment");
            var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
            var entity = new TableEntity(PK, rowKey) { { "DisplayName", InputSanitizer.StripHtml(body.DisplayName.Trim()) } };
            if (!string.IsNullOrWhiteSpace(body.Comment)) entity["Comment"] = InputSanitizer.StripHtml(body.Comment.Trim());
            if (!string.IsNullOrWhiteSpace(body.UserName)) entity["UserName"] = InputSanitizer.StripHtml(body.UserName.Trim());
            if (!string.IsNullOrWhiteSpace(body.Location)) entity["Location"] = InputSanitizer.StripHtml(body.Location.Trim());
            if (body.Latitude.HasValue) entity["Latitude"] = body.Latitude.Value;
            if (body.Longitude.HasValue) entity["Longitude"] = body.Longitude.Value;
            await table.AddEntityAsync(entity);
            return req.CreateJsonResponse(HttpStatusCode.Created, new { rowKey, displayName = InputSanitizer.StripHtml(body.DisplayName.Trim()) });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error creating equipment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("UpdateEquipment")]
    public async Task<HttpResponseData> UpdateEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/equipment/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        // equipment.location allows location-only edits; equipment.write allows full edit
        var (ctx, perms, pe) = await req.RequirePermissionAsync(_auth, "equipment.location"); if (ctx is null) return pe!;
        var (body, be) = await req.ReadJsonBodyAsync<EquipmentRequest>(); if (body is null) return be!;
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Equipment");
            var entity = (await table.GetEntityAsync<TableEntity>(PK, rowKey)).Value;
            bool hasWrite = perms is not null && PermissionChecker.HasPermission(perms, "equipment.write");
            if (hasWrite)
            {
                if (!string.IsNullOrWhiteSpace(body.DisplayName)) entity["DisplayName"] = InputSanitizer.StripHtml(body.DisplayName.Trim());
                if (body.Comment is not null) entity["Comment"] = InputSanitizer.StripHtml(body.Comment.Trim());
            }
            if (body.UserName is not null) entity["UserName"] = InputSanitizer.StripHtml(body.UserName.Trim());
            if (body.Location is not null) entity["Location"] = InputSanitizer.StripHtml(body.Location.Trim());
            if (body.Latitude.HasValue) entity["Latitude"] = body.Latitude.Value;
            if (body.Longitude.HasValue) entity["Longitude"] = body.Longitude.Value;
            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return req.CreateErrorResponse(HttpStatusCode.NotFound, "Equipment nicht gefunden."); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating equipment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("DeleteEquipment")]
    public async Task<HttpResponseData> DeleteEquipment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/equipment/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "equipment.write"); if (ctx is null) return pe!;
        try
        {
            await _tables.GetTableClient(ctx.TenantId, "Equipment").DeleteEntityAsync(PK, rowKey);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Gelöscht." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return req.CreateErrorResponse(HttpStatusCode.NotFound, "Equipment nicht gefunden."); }
        catch (Exception ex) { _logger.LogError(ex, "Error deleting equipment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private record EquipmentRequest(string? DisplayName, string? Comment, string? UserName, string? Location, double? Latitude, double? Longitude);
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
            if (await _adminAuth.ValidateTokenWithRole(req, 2) == 0)
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
                    comment = entity.GetString("Comment") ?? "",
                    userName = entity.GetString("UserName") ?? "",
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

    /// <summary>Lightweight members list for equipment location assignment (minRole 2).</summary>
    [Function("GetEquipmentMembers")]
    public async Task<IActionResult> GetEquipmentMembers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/equipment/members")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 2) == 0)
                return AdminAuth.Forbidden();

            var table = _tableService.GetTableClient("Users");
            await table.CreateIfNotExistsAsync();

            var members = new List<object>();
            await foreach (var entity in table.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'users'",
                select: new[] { "RowKey", "DisplayName", "Location", "Latitude", "Longitude" }))
            {
                var loc = entity.GetString("Location");
                var lat = entity.GetDouble("Latitude");
                var lng = entity.GetDouble("Longitude");
                if (!string.IsNullOrWhiteSpace(loc) && lat.HasValue && lng.HasValue)
                {
                    members.Add(new
                    {
                        displayName = entity.GetString("DisplayName") ?? entity.RowKey,
                        location = loc,
                        latitude = lat.Value,
                        longitude = lng.Value
                    });
                }
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            members.Sort((a, b) => comparer.Compare(
                ((dynamic)a).displayName as string ?? "",
                ((dynamic)b).displayName as string ?? ""));

            return new OkObjectResult(members);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading equipment members");
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
                { "DisplayName", InputSanitizer.StripHtml(body.DisplayName.Trim()) }
            };

            if (!string.IsNullOrWhiteSpace(body.Comment))
                entity["Comment"] = InputSanitizer.StripHtml(body.Comment.Trim());
            if (!string.IsNullOrWhiteSpace(body.UserName))
                entity["UserName"] = InputSanitizer.StripHtml(body.UserName.Trim());
            if (!string.IsNullOrWhiteSpace(body.Location))
                entity["Location"] = InputSanitizer.StripHtml(body.Location.Trim());
            if (body.Latitude.HasValue)
                entity["Latitude"] = body.Latitude.Value;
            if (body.Longitude.HasValue)
                entity["Longitude"] = body.Longitude.Value;

            await table.AddEntityAsync(entity);
            _logger.LogInformation("Equipment created: {Name}", body.DisplayName);

            return new CreatedResult("", new { rowKey, displayName = InputSanitizer.StripHtml(body.DisplayName.Trim()) });
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
            var callerLevel = await _adminAuth.ValidateTokenWithRole(req, 2);
            if (callerLevel == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<EquipmentRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null)
                return new BadRequestObjectResult(new { error = "Daten erforderlich" });

            var table = _tableService.GetTableClient(TableName);
            var response = await table.GetEntityAsync<TableEntity>(PK, rowKey);
            var entity = response.Value;

            // Manager+ can edit all fields; PowerUser can only edit location
            if (callerLevel >= 3)
            {
                if (!string.IsNullOrWhiteSpace(body.DisplayName))
                    entity["DisplayName"] = InputSanitizer.StripHtml(body.DisplayName.Trim());
                if (body.Comment is not null)
                    entity["Comment"] = InputSanitizer.StripHtml(body.Comment.Trim());
            }

            if (body.UserName is not null)
                entity["UserName"] = InputSanitizer.StripHtml(body.UserName.Trim());
            if (body.Location is not null)
                entity["Location"] = InputSanitizer.StripHtml(body.Location.Trim());
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
        public string? Comment { get; init; }
        public string? UserName { get; init; }
        public string? Location { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
    }
}
