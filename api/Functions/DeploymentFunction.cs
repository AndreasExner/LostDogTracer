using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class DeploymentFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<DeploymentFunction> _logger;

    public DeploymentFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<DeploymentFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _logger = logger;
    }

    [Function("GetDeploymentStatus")]
    public async Task<HttpResponseData> GetDeploymentStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deployments/status")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.own"); if (ctx is null) return pe!;
        try
        {
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            var user = await usersTable.GetEntityAsync<TableEntity>("users", ctx.Username, select: new[] { "DeploymentActive", "DeploymentDog", "DeploymentStart", "DeploymentKmStart", "DeploymentActivity" });
            return req.CreateJsonResponse(HttpStatusCode.OK, new
            {
                active = user.Value.GetBoolean("DeploymentActive") ?? false,
                dog = user.Value.GetString("DeploymentDog") ?? "",
                startTime = user.Value.GetString("DeploymentStart") ?? "",
                kmStart = user.Value.GetInt32("DeploymentKmStart"),
                activity = user.Value.GetString("DeploymentActivity") ?? ""
            });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return req.CreateJsonResponse(HttpStatusCode.OK, new { active = false, dog = "", startTime = "", kmStart = (int?)null, activity = "" }); }
        catch (Exception ex) { _logger.LogError(ex, "Error getting deployment status"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("StartDeployment")]
    public async Task<HttpResponseData> StartDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "deployments/start")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.own"); if (ctx is null) return pe!;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var dog = body.TryGetProperty("dog", out var d) ? d.GetString()?.Trim() : null;
            var activity = body.TryGetProperty("activity", out var a) ? a.GetString()?.Trim() : null;
            var kmStart = body.TryGetProperty("kmStart", out var km) && km.ValueKind == JsonValueKind.Number ? km.GetInt32() : (int?)null;
            if (string.IsNullOrWhiteSpace(dog)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Hund ist erforderlich.");

            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            var entity = (await usersTable.GetEntityAsync<TableEntity>("users", ctx.Username)).Value;
            if (entity.GetBoolean("DeploymentActive") == true) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Einsatz läuft bereits.");

            entity["DeploymentActive"] = true;
            entity["DeploymentDog"] = InputSanitizer.StripHtml(dog);
            entity["DeploymentStart"] = DateTimeOffset.UtcNow.ToString("o");
            if (!string.IsNullOrWhiteSpace(activity)) entity["DeploymentActivity"] = InputSanitizer.StripHtml(activity); else entity.Remove("DeploymentActivity");
            if (kmStart.HasValue) entity["DeploymentKmStart"] = kmStart.Value; else entity.Remove("DeploymentKmStart");
            await usersTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Einsatz gestartet", startTime = entity.GetString("DeploymentStart") });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error starting deployment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("EndDeployment")]
    public async Task<HttpResponseData> EndDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "deployments/end")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.own"); if (ctx is null) return pe!;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var kmEnd = body.TryGetProperty("kmEnd", out var km) && km.ValueKind == JsonValueKind.Number ? km.GetInt32() : (int?)null;
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            var entity = (await usersTable.GetEntityAsync<TableEntity>("users", ctx.Username)).Value;
            if (entity.GetBoolean("DeploymentActive") != true) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Kein Einsatz aktiv.");

            var dog = entity.GetString("DeploymentDog") ?? "";
            var startStr = entity.GetString("DeploymentStart") ?? "";
            var activity = entity.GetString("DeploymentActivity") ?? "";
            var kmStart = entity.GetInt32("DeploymentKmStart");
            var endTime = DateTimeOffset.UtcNow;
            int? kmDriven = kmStart.HasValue && kmEnd.HasValue && kmEnd.Value >= kmStart.Value ? kmEnd.Value - kmStart.Value : null;
            int durationMin = DateTimeOffset.TryParse(startStr, out var startDt) ? (int)(endTime - startDt).TotalMinutes : 0;

            var deplTable = _tables.GetTableClient(ctx.TenantId, "Deployments");
            var rowKey = (DateTimeOffset.MaxValue.Ticks - endTime.Ticks).ToString("D19");
            var record = new TableEntity(ctx.Username, rowKey) { { "LostDog", dog }, { "StartTime", startStr }, { "EndTime", endTime.ToString("o") }, { "Duration", durationMin } };
            if (kmStart.HasValue) record["KmStart"] = kmStart.Value;
            if (kmEnd.HasValue) record["KmEnd"] = kmEnd.Value;
            if (kmDriven.HasValue) record["KmDriven"] = kmDriven.Value;
            if (!string.IsNullOrWhiteSpace(activity)) record["Activity"] = activity;
            await deplTable.AddEntityAsync(record);

            entity["DeploymentActive"] = false; entity.Remove("DeploymentDog"); entity.Remove("DeploymentStart"); entity.Remove("DeploymentKmStart"); entity.Remove("DeploymentActivity");
            await usersTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Einsatz beendet", duration = durationMin, kmDriven });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error ending deployment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("GetDeployments")]
    public async Task<HttpResponseData> GetDeployments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deployments")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.own"); if (ctx is null) return pe!;
        try
        {
            var dogLookup = await BuildDogLookupAsync(ctx.TenantId);
            var dogFilter = req.GetQueryParam("dog");
            var table = _tables.GetTableClient(ctx.TenantId, "Deployments");
            var items = new List<object>();
            await foreach (var ent in table.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{ctx.Username}'"))
            {
                var entityDog = ent.GetString("LostDog") ?? "";
                if (!string.IsNullOrEmpty(dogFilter) && entityDog != dogFilter) continue;
                items.Add(new { rowKey = ent.RowKey, lostDog = dogLookup.GetValueOrDefault(entityDog, entityDog), lostDogKey = entityDog, activity = ent.GetString("Activity") ?? "", startTime = ent.GetString("StartTime") ?? "", endTime = ent.GetString("EndTime") ?? "", duration = ent.GetInt32("Duration") ?? 0, kmStart = ent.GetInt32("KmStart"), kmEnd = ent.GetInt32("KmEnd"), kmDriven = ent.GetInt32("KmDriven") });
            }
            var dogs = items.Select(i => new { key = ((dynamic)i).lostDogKey as string, name = ((dynamic)i).lostDog as string }).Where(d => !string.IsNullOrEmpty(d.key)).DistinctBy(d => d.key).OrderBy(d => d.name).Select(d => new { rowKey = d.key, displayName = d.name }).ToList();
            return req.CreateJsonResponse(HttpStatusCode.OK, new { records = items, lostDogs = dogs });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading deployments"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("CreateDeployment")]
    public async Task<HttpResponseData> CreateDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "deployments")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.own"); if (ctx is null) return pe!;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var dog = body.TryGetProperty("dog", out var d) ? d.GetString()?.Trim() : null;
            var startTime = body.TryGetProperty("startTime", out var s) ? s.GetString() : null;
            var endTimeStr = body.TryGetProperty("endTime", out var e2) ? e2.GetString() : null;
            var kmStart = body.TryGetProperty("kmStart", out var k1) && k1.ValueKind == JsonValueKind.Number ? k1.GetInt32() : (int?)null;
            var kmEnd = body.TryGetProperty("kmEnd", out var k2) && k2.ValueKind == JsonValueKind.Number ? k2.GetInt32() : (int?)null;
            var kmDrivenDirect = body.TryGetProperty("kmDriven", out var kd) && kd.ValueKind == JsonValueKind.Number ? kd.GetInt32() : (int?)null;
            if (string.IsNullOrWhiteSpace(dog) || string.IsNullOrWhiteSpace(startTime) || string.IsNullOrWhiteSpace(endTimeStr))
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Hund, Start- und Endzeit sind erforderlich.");
            if (!DateTimeOffset.TryParse(startTime, out var startDt) || !DateTimeOffset.TryParse(endTimeStr, out var endDt))
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültiges Datumsformat.");
            if (endDt <= startDt) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Endzeit muss nach Startzeit liegen.");

            int durationMin = (int)(endDt - startDt).TotalMinutes;
            int? kmDriven = kmDrivenDirect ?? (kmStart.HasValue && kmEnd.HasValue && kmEnd.Value >= kmStart.Value ? kmEnd.Value - kmStart.Value : null);
            var table = _tables.GetTableClient(ctx.TenantId, "Deployments");
            var rowKey = (DateTimeOffset.MaxValue.Ticks - endDt.Ticks).ToString("D19");
            var record = new TableEntity(ctx.Username, rowKey) { { "LostDog", InputSanitizer.StripHtml(dog) }, { "StartTime", startDt.ToString("o") }, { "EndTime", endDt.ToString("o") }, { "Duration", durationMin } };
            if (kmStart.HasValue) record["KmStart"] = kmStart.Value;
            if (kmEnd.HasValue) record["KmEnd"] = kmEnd.Value;
            if (kmDriven.HasValue) record["KmDriven"] = kmDriven.Value;
            await table.AddEntityAsync(record);
            return req.CreateJsonResponse(HttpStatusCode.Created, new { message = "Einsatz gespeichert." });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error creating deployment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("UpdateDeployment")]
    public async Task<HttpResponseData> UpdateDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "deployments/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.own"); if (ctx is null) return pe!;
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Deployments");
            var entity = (await table.GetEntityAsync<TableEntity>(ctx.Username, rowKey)).Value;
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            if (body.TryGetProperty("dog", out var d) && !string.IsNullOrWhiteSpace(d.GetString())) entity["LostDog"] = InputSanitizer.StripHtml(d.GetString()!);
            if (body.TryGetProperty("startTime", out var s) && DateTimeOffset.TryParse(s.GetString(), out var startDt)) entity["StartTime"] = startDt.ToString("o");
            if (body.TryGetProperty("endTime", out var e2) && DateTimeOffset.TryParse(e2.GetString(), out var endDt)) entity["EndTime"] = endDt.ToString("o");
            if (body.TryGetProperty("activity", out var act)) entity["Activity"] = InputSanitizer.StripHtml(act.GetString() ?? "");
            if (body.TryGetProperty("kmStart", out var k1)) { if (k1.ValueKind == JsonValueKind.Number) entity["KmStart"] = k1.GetInt32(); else entity.Remove("KmStart"); }
            if (body.TryGetProperty("kmEnd", out var k2)) { if (k2.ValueKind == JsonValueKind.Number) entity["KmEnd"] = k2.GetInt32(); else entity.Remove("KmEnd"); }
            if (DateTimeOffset.TryParse(entity.GetString("StartTime"), out var st) && DateTimeOffset.TryParse(entity.GetString("EndTime"), out var et) && et > st) entity["Duration"] = (int)(et - st).TotalMinutes;
            var ks = entity.GetInt32("KmStart"); var ke = entity.GetInt32("KmEnd");
            if (ks.HasValue && ke.HasValue && ke.Value >= ks.Value) entity["KmDriven"] = ke.Value - ks.Value; else entity.Remove("KmDriven");
            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Einsatz aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return req.CreateErrorResponse(HttpStatusCode.NotFound, "Einsatz nicht gefunden."); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating deployment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("DeleteDeployment")]
    public async Task<HttpResponseData> DeleteDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "deployments/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.own"); if (ctx is null) return pe!;
        try
        {
            await _tables.GetTableClient(ctx.TenantId, "Deployments").DeleteEntityAsync(ctx.Username, rowKey);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Einsatz gelöscht." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return req.CreateErrorResponse(HttpStatusCode.NotFound, "Einsatz nicht gefunden."); }
        catch (Exception ex) { _logger.LogError(ex, "Error deleting deployment"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("GetAllDeployments")]
    public async Task<HttpResponseData> GetAllDeployments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deployments/accounting")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "deployments.manage"); if (ctx is null) return pe!;
        try
        {
            var dogLookup = await BuildDogLookupAsync(ctx.TenantId);
            var userLookup = await BuildUserLookupAsync(ctx.TenantId);
            var filterUser = req.GetQueryParam("user"); var filterDog = req.GetQueryParam("dog");
            var filterFrom = req.GetQueryParam("from"); var filterTo = req.GetQueryParam("to");
            DateTimeOffset? fromDt = null, toDt = null;
            if (!string.IsNullOrEmpty(filterFrom) && DateTimeOffset.TryParse(filterFrom, out var f)) fromDt = f;
            if (!string.IsNullOrEmpty(filterTo) && DateTimeOffset.TryParse(filterTo, out var t)) toDt = t.AddDays(1);

            var table = _tables.GetTableClient(ctx.TenantId, "Deployments");
            var items = new List<object>();
            await foreach (var ent in table.QueryAsync<TableEntity>())
            {
                var user = ent.PartitionKey;
                if (!string.IsNullOrEmpty(filterUser) && !string.Equals(user, filterUser, StringComparison.OrdinalIgnoreCase)) continue;
                var entityDog = ent.GetString("LostDog") ?? "";
                if (!string.IsNullOrEmpty(filterDog) && entityDog != filterDog) continue;
                if (fromDt.HasValue || toDt.HasValue)
                {
                    var endStr = ent.GetString("EndTime");
                    if (!string.IsNullOrEmpty(endStr) && DateTimeOffset.TryParse(endStr, out var endDtParsed))
                    { if (fromDt.HasValue && endDtParsed < fromDt.Value) continue; if (toDt.HasValue && endDtParsed >= toDt.Value) continue; }
                }
                items.Add(new { rowKey = ent.RowKey, user, userName = userLookup.GetValueOrDefault(user, user), lostDog = dogLookup.GetValueOrDefault(entityDog, entityDog), lostDogKey = entityDog, activity = ent.GetString("Activity") ?? "", startTime = ent.GetString("StartTime") ?? "", endTime = ent.GetString("EndTime") ?? "", duration = ent.GetInt32("Duration") ?? 0, kmStart = ent.GetInt32("KmStart"), kmEnd = ent.GetInt32("KmEnd"), kmDriven = ent.GetInt32("KmDriven") });
            }
            var dogs = items.Select(i => new { key = ((dynamic)i).lostDogKey as string, name = ((dynamic)i).lostDog as string }).Where(d => !string.IsNullOrEmpty(d.key)).DistinctBy(d => d.key).OrderBy(d => d.name).Select(d => new { rowKey = d.key, displayName = d.name }).ToList();
            var users = items.Select(i => new { key = ((dynamic)i).user as string, name = ((dynamic)i).userName as string }).Where(u => !string.IsNullOrEmpty(u.key)).DistinctBy(u => u.key).OrderBy(u => u.name).Select(u => new { rowKey = u.key, displayName = u.name }).ToList();
            return req.CreateJsonResponse(HttpStatusCode.OK, new { records = items, lostDogs = dogs, users });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading accounting"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private async Task<Dictionary<string, string>> BuildDogLookupAsync(string tenantId)
    {
        var lookup = new Dictionary<string, string>();
        var table = _tables.GetTableClient(tenantId, "LostDogs");
        await foreach (var e in table.QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName" }))
            lookup[e.RowKey] = e.GetString("DisplayName") ?? e.RowKey;
        return lookup;
    }

    private async Task<Dictionary<string, string>> BuildUserLookupAsync(string tenantId)
    {
        var lookup = new Dictionary<string, string>();
        var table = _tables.GetTableClient(tenantId, "Users");
        await foreach (var e in table.QueryAsync<TableEntity>(filter: "PartitionKey eq 'users'", select: new[] { "RowKey", "DisplayName" }))
            lookup[e.RowKey] = e.GetString("DisplayName") ?? e.RowKey;
        return lookup;
    }
}

    /// <summary>Validate token with minRole and return username (or null).</summary>
    private async Task<string?> ValidateAndGetUser(HttpRequest req, int minRole = 1)
    {
        if (await _adminAuth.ValidateTokenWithRole(req, minRole) == 0) return null;
        return _adminAuth.GetUsernameFromToken(req);
    }

    // ── Get deployment status for current user ───────────────────
    [Function("GetDeploymentStatus")]
    public async Task<IActionResult> GetDeploymentStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deployments/status")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();

            var usersTable = _tableService.GetTableClient(UsersTable);
            var userEntity = await usersTable.GetEntityAsync<TableEntity>("users", username,
                select: new[] { "DeploymentActive", "DeploymentDog", "DeploymentStart", "DeploymentKmStart", "DeploymentActivity" });

            return new OkObjectResult(new
            {
                active = userEntity.Value.GetBoolean("DeploymentActive") ?? false,
                dog = userEntity.Value.GetString("DeploymentDog") ?? "",
                startTime = userEntity.Value.GetString("DeploymentStart") ?? "",
                kmStart = userEntity.Value.GetInt32("DeploymentKmStart"),
                activity = userEntity.Value.GetString("DeploymentActivity") ?? ""
            });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return new OkObjectResult(new { active = false, dog = "", startTime = "", kmStart = (int?)null });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting deployment status");
            return new StatusCodeResult(500);
        }
    }

    // ── Start deployment (clock in) ──────────────────────────────
    [Function("StartDeployment")]
    public async Task<IActionResult> StartDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "deployments/start")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var dog = body.TryGetProperty("dog", out var d) ? d.GetString()?.Trim() : null;
            var activity = body.TryGetProperty("activity", out var a) ? a.GetString()?.Trim() : null;
            var kmStart = body.TryGetProperty("kmStart", out var km) && km.ValueKind == JsonValueKind.Number ? km.GetInt32() : (int?)null;

            if (string.IsNullOrWhiteSpace(dog))
                return new BadRequestObjectResult(new { error = "Hund ist erforderlich" });

            var usersTable = _tableService.GetTableClient(UsersTable);
            var userEntity = await usersTable.GetEntityAsync<TableEntity>("users", username);
            var entity = userEntity.Value;

            if (entity.GetBoolean("DeploymentActive") == true)
                return new BadRequestObjectResult(new { error = "Einsatz läuft bereits" });

            entity["DeploymentActive"] = true;
            entity["DeploymentDog"] = InputSanitizer.StripHtml(dog);
            entity["DeploymentStart"] = DateTimeOffset.UtcNow.ToString("o");
            if (!string.IsNullOrWhiteSpace(activity))
                entity["DeploymentActivity"] = InputSanitizer.StripHtml(activity);
            else
                entity.Remove("DeploymentActivity");
            if (kmStart.HasValue)
                entity["DeploymentKmStart"] = kmStart.Value;
            else
                entity.Remove("DeploymentKmStart");

            await usersTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);

            return new OkObjectResult(new { message = "Einsatz gestartet", startTime = entity.GetString("DeploymentStart") });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting deployment");
            return new StatusCodeResult(500);
        }
    }

    // ── End deployment (clock out) ───────────────────────────────
    [Function("EndDeployment")]
    public async Task<IActionResult> EndDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "deployments/end")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var kmEnd = body.TryGetProperty("kmEnd", out var km) && km.ValueKind == JsonValueKind.Number ? km.GetInt32() : (int?)null;

            var usersTable = _tableService.GetTableClient(UsersTable);
            var userEntity = await usersTable.GetEntityAsync<TableEntity>("users", username);
            var entity = userEntity.Value;

            if (entity.GetBoolean("DeploymentActive") != true)
                return new BadRequestObjectResult(new { error = "Kein Einsatz aktiv" });

            var dog = entity.GetString("DeploymentDog") ?? "";
            var startStr = entity.GetString("DeploymentStart") ?? "";
            var activity = entity.GetString("DeploymentActivity") ?? "";
            var kmStart = entity.GetInt32("DeploymentKmStart");
            var endTime = DateTimeOffset.UtcNow;

            int? kmDriven = null;
            if (kmStart.HasValue && kmEnd.HasValue && kmEnd.Value >= kmStart.Value)
                kmDriven = kmEnd.Value - kmStart.Value;

            int durationMin = 0;
            if (DateTimeOffset.TryParse(startStr, out var startDt))
                durationMin = (int)(endTime - startDt).TotalMinutes;

            // Save deployment record
            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            var rowKey = (DateTimeOffset.MaxValue.Ticks - endTime.Ticks).ToString("D19");
            var record = new TableEntity(username, rowKey)
            {
                { "LostDog", dog },
                { "StartTime", startStr },
                { "EndTime", endTime.ToString("o") },
                { "Duration", durationMin }
            };
            if (kmStart.HasValue) record["KmStart"] = kmStart.Value;
            if (kmEnd.HasValue) record["KmEnd"] = kmEnd.Value;
            if (kmDriven.HasValue) record["KmDriven"] = kmDriven.Value;
            if (!string.IsNullOrWhiteSpace(activity)) record["Activity"] = activity;

            await table.AddEntityAsync(record);

            // Clear deployment state from user
            entity["DeploymentActive"] = false;
            entity.Remove("DeploymentDog");
            entity.Remove("DeploymentStart");
            entity.Remove("DeploymentKmStart");
            entity.Remove("DeploymentActivity");
            await usersTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);

            _logger.LogInformation("Deployment ended: {User} for {Dog}, {Duration}min",
                username, dog, durationMin);

            return new OkObjectResult(new
            {
                message = "Einsatz beendet",
                duration = durationMin,
                kmDriven
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ending deployment");
            return new StatusCodeResult(500);
        }
    }

    // ── List own deployments ─────────────────────────────────────
    [Function("GetDeployments")]
    public async Task<IActionResult> GetDeployments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deployments")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();

            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            // Resolve dog display names
            var dogLookup = new Dictionary<string, string>();
            var dogTable = _tableService.GetTableClient("LostDogs");
            await dogTable.CreateIfNotExistsAsync();
            await foreach (var e in dogTable.QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Location" }))
                dogLookup[e.RowKey] = e.GetString("DisplayName") ?? e.GetString("Location") ?? e.RowKey;

            var dog = req.Query["dog"].FirstOrDefault();
            var filter = $"PartitionKey eq '{username}'";

            var items = new List<object>();
            await foreach (var entity in table.QueryAsync<TableEntity>(filter: filter))
            {
                var entityDog = entity.GetString("LostDog") ?? "";
                var displayDog = dogLookup.GetValueOrDefault(entityDog, entityDog);
                if (!string.IsNullOrEmpty(dog) && entityDog != dog) continue;

                items.Add(new
                {
                    rowKey = entity.RowKey,
                    lostDog = displayDog,
                    lostDogKey = entityDog,
                    activity = entity.GetString("Activity") ?? "",
                    startTime = entity.GetString("StartTime") ?? "",
                    endTime = entity.GetString("EndTime") ?? "",
                    duration = entity.GetInt32("Duration") ?? 0,
                    kmStart = entity.GetInt32("KmStart"),
                    kmEnd = entity.GetInt32("KmEnd"),
                    kmDriven = entity.GetInt32("KmDriven")
                });
            }

            // Collect unique dogs for filter dropdown
            var dogs = items.Select(i => new { key = ((dynamic)i).lostDogKey as string, name = ((dynamic)i).lostDog as string })
                .Where(d => !string.IsNullOrEmpty(d.key))
                .DistinctBy(d => d.key)
                .OrderBy(d => d.name, StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false))
                .Select(d => new { rowKey = d.key, displayName = d.name })
                .ToList();

            return new OkObjectResult(new { records = items, lostDogs = dogs });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading deployments");
            return new StatusCodeResult(500);
        }
    }

    // ── Create manual deployment ─────────────────────────────────
    [Function("CreateDeployment")]
    public async Task<IActionResult> CreateDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "deployments")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var dog = body.TryGetProperty("dog", out var d) ? d.GetString()?.Trim() : null;
            var startTime = body.TryGetProperty("startTime", out var s) ? s.GetString() : null;
            var endTime = body.TryGetProperty("endTime", out var e2) ? e2.GetString() : null;
            var kmStart = body.TryGetProperty("kmStart", out var k1) && k1.ValueKind == JsonValueKind.Number ? k1.GetInt32() : (int?)null;
            var kmEnd = body.TryGetProperty("kmEnd", out var k2) && k2.ValueKind == JsonValueKind.Number ? k2.GetInt32() : (int?)null;
            var kmDrivenDirect = body.TryGetProperty("kmDriven", out var kd) && kd.ValueKind == JsonValueKind.Number ? kd.GetInt32() : (int?)null;

            if (string.IsNullOrWhiteSpace(dog) || string.IsNullOrWhiteSpace(startTime) || string.IsNullOrWhiteSpace(endTime))
                return new BadRequestObjectResult(new { error = "Hund, Start- und Endzeit sind erforderlich" });

            if (!DateTimeOffset.TryParse(startTime, out var startDt) || !DateTimeOffset.TryParse(endTime, out var endDt))
                return new BadRequestObjectResult(new { error = "Ungültiges Datumsformat" });

            if (endDt <= startDt)
                return new BadRequestObjectResult(new { error = "Endzeit muss nach Startzeit liegen" });

            int durationMin = (int)(endDt - startDt).TotalMinutes;
            int? kmDriven = kmDrivenDirect
                ?? ((kmStart.HasValue && kmEnd.HasValue && kmEnd.Value >= kmStart.Value) ? kmEnd.Value - kmStart.Value : null);

            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            var rowKey = (DateTimeOffset.MaxValue.Ticks - endDt.Ticks).ToString("D19");
            var record = new TableEntity(username, rowKey)
            {
                { "LostDog", InputSanitizer.StripHtml(dog) },
                { "StartTime", startDt.ToString("o") },
                { "EndTime", endDt.ToString("o") },
                { "Duration", durationMin }
            };
            if (kmStart.HasValue) record["KmStart"] = kmStart.Value;
            if (kmEnd.HasValue) record["KmEnd"] = kmEnd.Value;
            if (kmDriven.HasValue) record["KmDriven"] = kmDriven.Value;

            await table.AddEntityAsync(record);

            return new CreatedResult("", new { message = "Einsatz gespeichert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating deployment");
            return new StatusCodeResult(500);
        }
    }

    // ── Update deployment ────────────────────────────────────────
    [Function("UpdateDeployment")]
    public async Task<IActionResult> UpdateDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "deployments/{rowKey}")] HttpRequest req,
        string rowKey)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();

            var table = _tableService.GetTableClient(TableName);
            var existing = await table.GetEntityAsync<TableEntity>(username, rowKey);
            var entity = existing.Value;

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);

            if (body.TryGetProperty("dog", out var d) && !string.IsNullOrWhiteSpace(d.GetString()))
                entity["LostDog"] = InputSanitizer.StripHtml(d.GetString()!);
            if (body.TryGetProperty("startTime", out var s) && DateTimeOffset.TryParse(s.GetString(), out var startDt))
                entity["StartTime"] = startDt.ToString("o");
            if (body.TryGetProperty("endTime", out var e2) && DateTimeOffset.TryParse(e2.GetString(), out var endDt))
                entity["EndTime"] = endDt.ToString("o");
            if (body.TryGetProperty("activity", out var act))
                entity["Activity"] = InputSanitizer.StripHtml(act.GetString() ?? "");

            if (body.TryGetProperty("kmStart", out var k1))
            {
                if (k1.ValueKind == JsonValueKind.Number) entity["KmStart"] = k1.GetInt32();
                else entity.Remove("KmStart");
            }
            if (body.TryGetProperty("kmEnd", out var k2))
            {
                if (k2.ValueKind == JsonValueKind.Number) entity["KmEnd"] = k2.GetInt32();
                else entity.Remove("KmEnd");
            }

            // Recalculate duration and km
            if (DateTimeOffset.TryParse(entity.GetString("StartTime"), out var st) &&
                DateTimeOffset.TryParse(entity.GetString("EndTime"), out var et) && et > st)
                entity["Duration"] = (int)(et - st).TotalMinutes;

            var ks = entity.GetInt32("KmStart");
            var ke = entity.GetInt32("KmEnd");
            if (ks.HasValue && ke.HasValue && ke.Value >= ks.Value)
                entity["KmDriven"] = ke.Value - ks.Value;
            else
                entity.Remove("KmDriven");

            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);

            return new OkObjectResult(new { message = "Einsatz aktualisiert" });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return new NotFoundObjectResult(new { error = "Einsatz nicht gefunden" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating deployment");
            return new StatusCodeResult(500);
        }
    }

    // ── Delete deployment ────────────────────────────────────────
    [Function("DeleteDeployment")]
    public async Task<IActionResult> DeleteDeployment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "deployments/{rowKey}")] HttpRequest req,
        string rowKey)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();

            var table = _tableService.GetTableClient(TableName);
            await table.DeleteEntityAsync(username, rowKey);

            return new OkObjectResult(new { message = "Einsatz gelöscht" });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return new NotFoundObjectResult(new { error = "Einsatz nicht gefunden" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting deployment");
            return new StatusCodeResult(500);
        }
    }

    // ── Accounting: all deployments (Accountant only) ────────────
    [Function("GetAllDeployments")]
    public async Task<IActionResult> GetAllDeployments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "deployments/accounting")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var username = await ValidateAndGetUser(req, 1);
            if (username == null)
                return AdminAuth.Forbidden();
            if (!await _adminAuth.IsAccountantAsync(username))
                return AdminAuth.Forbidden();

            var table = _tableService.GetTableClient(TableName);
            await table.CreateIfNotExistsAsync();

            // Resolve dog display names
            var dogLookup = new Dictionary<string, string>();
            var dogTable = _tableService.GetTableClient("LostDogs");
            await dogTable.CreateIfNotExistsAsync();
            await foreach (var e in dogTable.QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Location" }))
                dogLookup[e.RowKey] = e.GetString("DisplayName") ?? e.GetString("Location") ?? e.RowKey;

            // Resolve user display names
            var userLookup = new Dictionary<string, string>();
            var usersTable = _tableService.GetTableClient("Users");
            await foreach (var e in usersTable.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'users'", select: new[] { "RowKey", "DisplayName" }))
                userLookup[e.RowKey] = e.GetString("DisplayName") ?? e.RowKey;

            // Optional filters
            var filterUser = req.Query["user"].FirstOrDefault();
            var filterDog = req.Query["dog"].FirstOrDefault();
            var filterFrom = req.Query["from"].FirstOrDefault();
            var filterTo = req.Query["to"].FirstOrDefault();

            DateTimeOffset? fromDt = null, toDt = null;
            if (!string.IsNullOrEmpty(filterFrom) && DateTimeOffset.TryParse(filterFrom, out var f)) fromDt = f;
            if (!string.IsNullOrEmpty(filterTo) && DateTimeOffset.TryParse(filterTo, out var t)) toDt = t.AddDays(1); // inclusive end

            var items = new List<object>();
            await foreach (var entity in table.QueryAsync<TableEntity>())
            {
                var user = entity.PartitionKey;
                if (!string.IsNullOrEmpty(filterUser) && !string.Equals(user, filterUser, StringComparison.OrdinalIgnoreCase))
                    continue;

                var entityDog = entity.GetString("LostDog") ?? "";
                if (!string.IsNullOrEmpty(filterDog) && entityDog != filterDog) continue;

                var startStr = entity.GetString("StartTime") ?? "";
                if (DateTimeOffset.TryParse(startStr, out var startDt))
                {
                    if (fromDt.HasValue && startDt < fromDt.Value) continue;
                    if (toDt.HasValue && startDt >= toDt.Value) continue;
                }

                items.Add(new
                {
                    rowKey = entity.RowKey,
                    user,
                    userDisplay = userLookup.GetValueOrDefault(user, user),
                    lostDog = dogLookup.GetValueOrDefault(entityDog, entityDog),
                    lostDogKey = entityDog,
                    activity = entity.GetString("Activity") ?? "",
                    startTime = startStr,
                    endTime = entity.GetString("EndTime") ?? "",
                    duration = entity.GetInt32("Duration") ?? 0,
                    kmStart = entity.GetInt32("KmStart"),
                    kmEnd = entity.GetInt32("KmEnd"),
                    kmDriven = entity.GetInt32("KmDriven")
                });
            }

            // Collect unique users and dogs for filter dropdowns
            var users = items.Select(i => ((dynamic)i).user as string)
                .Where(u => !string.IsNullOrEmpty(u)).Distinct()
                .OrderBy(u => userLookup.GetValueOrDefault(u!, u!), StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false))
                .Select(u => new { username = u, displayName = userLookup.GetValueOrDefault(u!, u!) })
                .ToList();

            var dogs = items.Select(i => new { key = ((dynamic)i).lostDogKey as string, name = ((dynamic)i).lostDog as string })
                .Where(d => !string.IsNullOrEmpty(d.key))
                .DistinctBy(d => d.key)
                .OrderBy(d => d.name, StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false))
                .Select(d => new { rowKey = d.key, displayName = d.name })
                .ToList();

            return new OkObjectResult(new { records = items, users, lostDogs = dogs });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading accounting data");
            return new StatusCodeResult(500);
        }
    }
}
