using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class GPSRecordsFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<GPSRecordsFunction> _logger;

    public GPSRecordsFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, BlobServiceClient blobService, ILogger<GPSRecordsFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _blobService = blobService; _logger = logger;
    }

    [Function("GetGPSRecords")]
    public async Task<HttpResponseData> GetGPSRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/gps-records")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "gps.read"); if (ctx is null) return pe!;
        try
        {
            var dogLookup = new Dictionary<string, string>();
            await foreach (var ent in _tables.GetTableClient(ctx.TenantId, "LostDogs").QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName" }))
                dogLookup[ent.RowKey] = ent.GetString("DisplayName") ?? ent.RowKey;
            var catLookup = new Dictionary<string, string>();
            await foreach (var ent in _tables.GetTableClient(ctx.TenantId, "Categories").QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Name" }))
                catLookup[ent.RowKey] = ent.GetString("DisplayName") ?? ent.GetString("Name") ?? ent.RowKey;
            var userLookup = new Dictionary<string, string>();
            await foreach (var ent in _tables.GetTableClient(ctx.TenantId, "Users").QueryAsync<TableEntity>(filter: "PartitionKey eq 'users'", select: new[] { "RowKey", "DisplayName" }))
                userLookup[ent.RowKey] = ent.GetString("DisplayName") ?? ent.RowKey;
            var guestNickLookup = new Dictionary<string, string>();
            try { await foreach (var ent in _tables.GetTableClient(ctx.TenantId, "GuestTokens").QueryAsync<TableEntity>(filter: "PartitionKey eq 'guest'", select: new[] { "Token", "NickName" }))
            { var tk = ent.GetString("Token") ?? ""; var nk = ent.GetString("NickName") ?? ""; if (!string.IsNullOrEmpty(tk) && !string.IsNullOrEmpty(nk)) guestNickLookup[tk] = nk; } } catch { }

            var lostDogFilter = req.GetQueryParam("lostDog"); var nameFilter = req.GetQueryParam("name");
            var categoryFilterRaw = req.GetQueryParam("category");
            var categoryFilters = string.IsNullOrEmpty(categoryFilterRaw) ? Array.Empty<string>() : categoryFilterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var pageSizeStr = req.GetQueryParam("pageSize"); var pageStr = req.GetQueryParam("page");
            int? pageSize = pageSizeStr == "all" ? null : int.TryParse(pageSizeStr, out var ps) ? ps : 20;
            int page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;

            var allRecords = new List<object>(); var allDogKeys = new HashSet<string>(); var allNameKeys = new HashSet<string>(); var allCatKeys = new HashSet<string>();
            await foreach (var entity in _tables.GetTableClient(ctx.TenantId, "GPSRecords").QueryAsync<TableEntity>())
            {
                var ldk = entity.GetString("LostDog") ?? ""; var nk = entity.PartitionKey ?? ""; var ck = entity.GetString("Category") ?? "";
                if (!string.IsNullOrEmpty(lostDogFilter) && ldk != lostDogFilter) continue;
                if (!string.IsNullOrEmpty(nameFilter) && nk != nameFilter) continue;
                if (categoryFilters.Length > 0 && !categoryFilters.Contains(ck)) continue;
                if (!string.IsNullOrEmpty(ldk)) allDogKeys.Add(ldk);
                if (!string.IsNullOrEmpty(nk)) allNameKeys.Add(nk);
                if (!string.IsNullOrEmpty(ck)) allCatKeys.Add(ck);
                string displayName;
                if (nk == "GUEST") { var gt = entity.GetString("GuestToken") ?? ""; displayName = !string.IsNullOrEmpty(gt) && guestNickLookup.TryGetValue(gt, out var nick) ? $"Gast: {nick}" : "Gast-Helfer*in"; }
                else displayName = userLookup.GetValueOrDefault(nk, nk);
                allRecords.Add(new { partitionKey = entity.PartitionKey, rowKey = entity.RowKey, nameKey = nk, name = displayName, lostDogKey = ldk, lostDog = dogLookup.GetValueOrDefault(ldk, ldk), latitude = GetDoubleSafe(entity, "Latitude"), longitude = GetDoubleSafe(entity, "Longitude"), accuracy = GetDoubleSafe(entity, "Accuracy"), recordedAt = entity.GetString("RecordedAt") ?? entity.Timestamp?.ToString("o") ?? "", photoUrl = entity.GetString("PhotoUrl") ?? "", comment = entity.GetString("Comment") ?? "", categoryKey = ck, category = catLookup.GetValueOrDefault(ck, ck) });
            }
            int totalCount = allRecords.Count;
            var deComp = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            var lostDogs = allDogKeys.Select(k => new { rowKey = k, displayName = dogLookup.GetValueOrDefault(k, k) }).OrderBy(x => x.displayName, deComp).ToList();
            var names = allNameKeys.Select(k => new { rowKey = k, displayName = k == "GUEST" ? "Gast-Helfer*in" : userLookup.GetValueOrDefault(k, k) }).OrderBy(x => x.displayName, deComp).ToList();
            var categories = allCatKeys.Select(k => new { rowKey = k, displayName = catLookup.GetValueOrDefault(k, k) }).OrderBy(x => x.displayName, deComp).ToList();
            IEnumerable<object> pagedRecords = pageSize.HasValue ? allRecords.Skip((page - 1) * pageSize.Value).Take(pageSize.Value) : allRecords;
            return req.CreateJsonResponse(HttpStatusCode.OK, new { records = pagedRecords, totalCount, page, pageSize = pageSize ?? totalCount, totalPages = pageSize.HasValue ? (int)Math.Ceiling((double)totalCount / pageSize.Value) : 1, lostDogs, names, categories });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading GPS records"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("DeleteGPSRecords")]
    public async Task<HttpResponseData> DeleteGPSRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/gps-records/delete")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "gps.delete"); if (ctx is null) return pe!;
        var (body, be) = await req.ReadJsonBodyAsync<List<DeleteKey>>(); if (body is null) return be!;
        if (body.Count == 0) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Keine Einträge zum Löschen.");
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "GPSRecords");
            var container = _blobService.GetBlobContainerClient(BlobHelper.PhotoContainer);
            int deleted = 0;
            foreach (var key in body)
            {
                try
                {
                    try { var ent = await table.GetEntityAsync<TableEntity>(key.PartitionKey, key.RowKey); var photoUrl = ent.Value.GetString("PhotoUrl"); if (!string.IsNullOrEmpty(photoUrl)) { var blobName = BlobHelper.ExtractBlobName(photoUrl); if (blobName is not null) try { await container.DeleteBlobIfExistsAsync(blobName); } catch { } } } catch { }
                    await table.DeleteEntityAsync(key.PartitionKey, key.RowKey); deleted++;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { deleted, message = $"{deleted} Einträge gelöscht." });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error deleting GPS records"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("GetMyRecords")]
    public async Task<HttpResponseData> GetMyRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "my-records")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId)) { var (c, _) = req.ValidateToken(_auth); if (c is not null) tenantId = c.TenantId; }
        if (string.IsNullOrWhiteSpace(tenantId)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");
        var nameFilter = req.GetQueryParam("name"); var lostDogFilter = req.GetQueryParam("lostDog");
        var guestTokenFilter = req.GetQueryParam("guestToken") ?? ""; var ownerKeyFilter = req.GetQueryParam("ownerKey") ?? "";
        if (string.IsNullOrWhiteSpace(lostDogFilter)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "lostDog ist erforderlich.");
        try
        {
            var dogLookup = new Dictionary<string, string>();
            await foreach (var ent in _tables.GetTableClient(tenantId, "LostDogs").QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName" }))
                dogLookup[ent.RowKey] = ent.GetString("DisplayName") ?? ent.RowKey;
            var catLookup = new Dictionary<string, string>();
            await foreach (var ent in _tables.GetTableClient(tenantId, "Categories").QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Name" }))
                catLookup[ent.RowKey] = ent.GetString("DisplayName") ?? ent.GetString("Name") ?? ent.RowKey;
            var userLookup = new Dictionary<string, string>();
            await foreach (var ent in _tables.GetTableClient(tenantId, "Users").QueryAsync<TableEntity>(filter: "PartitionKey eq 'users'", select: new[] { "RowKey", "DisplayName" }))
                userLookup[ent.RowKey] = ent.GetString("DisplayName") ?? ent.RowKey;

            var pageSizeStr = req.GetQueryParam("pageSize"); var pageStr = req.GetQueryParam("page");
            int? pageSize = pageSizeStr == "all" ? null : int.TryParse(pageSizeStr, out var ps) ? ps : 20;
            int page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
            string? tableFilter = null;
            if (!string.IsNullOrWhiteSpace(nameFilter))
            {
                if (nameFilter.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(nameFilter, @"^[a-zA-Z0-9\-_\*]+$"))
                    return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültiger Name-Filter.");
                tableFilter = $"PartitionKey eq '{nameFilter}'";
            }
            var allRecords = new List<object>();
            await foreach (var entity in _tables.GetTableClient(tenantId, "GPSRecords").QueryAsync<TableEntity>(filter: tableFilter))
            {
                var ldk = entity.GetString("LostDog") ?? ""; if (ldk != lostDogFilter) continue;
                var ck = entity.GetString("Category") ?? "";
                var recordToken = entity.GetString("GuestToken") ?? ""; var recordOwnerKey = entity.GetString("OwnerKey") ?? "";
                var isOwner = (!string.IsNullOrEmpty(guestTokenFilter) && recordToken == guestTokenFilter) || (!string.IsNullOrEmpty(ownerKeyFilter) && recordOwnerKey == ownerKeyFilter);
                allRecords.Add(new { partitionKey = entity.PartitionKey, rowKey = entity.RowKey, name = userLookup.GetValueOrDefault(entity.PartitionKey, entity.PartitionKey), lostDogKey = ldk, lostDog = dogLookup.GetValueOrDefault(ldk, ldk), latitude = GetDoubleSafe(entity, "Latitude"), longitude = GetDoubleSafe(entity, "Longitude"), accuracy = GetDoubleSafe(entity, "Accuracy"), recordedAt = entity.GetString("RecordedAt") ?? entity.Timestamp?.ToString("o") ?? "", photoUrl = entity.GetString("PhotoUrl") ?? "", comment = entity.GetString("Comment") ?? "", categoryKey = ck, category = catLookup.GetValueOrDefault(ck, ck), isOwner });
            }
            int totalCount = allRecords.Count;
            IEnumerable<object> pagedRecords = pageSize.HasValue ? allRecords.Skip((page - 1) * pageSize.Value).Take(pageSize.Value) : allRecords;
            return req.CreateJsonResponse(HttpStatusCode.OK, new { records = pagedRecords, totalCount, page, pageSize = pageSize ?? totalCount, totalPages = pageSize.HasValue ? (int)Math.Ceiling((double)totalCount / pageSize.Value) : 1 });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading my records"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("DeleteMyRecords")]
    public async Task<HttpResponseData> DeleteMyRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "my-records/delete")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (body, be) = await req.ReadJsonBodyAsync<MyDeleteRequest>(); if (body is null) return be!;
        if (string.IsNullOrWhiteSpace(body.LostDog) || string.IsNullOrWhiteSpace(body.TenantId)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId und lostDog sind erforderlich.");
        if (body.Keys is null || body.Keys.Count == 0) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Keine Einträge zum Löschen.");
        try
        {
            var table = _tables.GetTableClient(body.TenantId, "GPSRecords");
            var container = _blobService.GetBlobContainerClient(BlobHelper.PhotoContainer);
            int deleted = 0;
            foreach (var key in body.Keys)
            {
                try
                {
                    var ent = (await table.GetEntityAsync<TableEntity>(key.PartitionKey, key.RowKey)).Value;
                    if ((ent.GetString("LostDog") ?? "") != body.LostDog) continue;
                    if (!string.IsNullOrEmpty(body.GuestToken) && (ent.GetString("GuestToken") ?? "") != body.GuestToken) continue;
                    if (!string.IsNullOrEmpty(body.OwnerKey) && (ent.GetString("OwnerKey") ?? "") != body.OwnerKey) continue;
                    var photoUrl = ent.GetString("PhotoUrl"); if (!string.IsNullOrEmpty(photoUrl)) { var bn = BlobHelper.ExtractBlobName(photoUrl); if (bn is not null) try { await container.DeleteBlobIfExistsAsync(bn); } catch { } }
                    await table.DeleteEntityAsync(key.PartitionKey, key.RowKey); deleted++;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { deleted, message = $"{deleted} Einträge gelöscht." });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error deleting my records"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private static double? GetDoubleSafe(TableEntity entity, string key)
    {
        try { var d = entity.GetDouble(key); if (d.HasValue) return d.Value; } catch { }
        try { var i = entity.GetInt64(key); if (i.HasValue) return (double)i.Value; } catch { }
        try { var i = entity.GetInt32(key); if (i.HasValue) return (double)i.Value; } catch { }
        return null;
    }

    private record DeleteKey(string PartitionKey, string RowKey);
    private record MyDeleteRequest(string? TenantId, string? Name, string? LostDog, string? GuestToken, string? OwnerKey, List<DeleteKey>? Keys);
}
    }

    [Function("GetGPSRecords")]
    public async Task<IActionResult> GetGPSRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/gps-records")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 1) == 0)
                return AdminAuth.Forbidden();

            // Build lookup maps for FK resolution
            var dogLookup = new Dictionary<string, string>();
            var dogTable = _tableService.GetTableClient("LostDogs");
            await dogTable.CreateIfNotExistsAsync();
            await foreach (var e in dogTable.QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Location" }))
                dogLookup[e.RowKey] = e.GetString("DisplayName") ?? e.GetString("Location") ?? e.RowKey;

            var catLookup = new Dictionary<string, string>();
            var catTable = _tableService.GetTableClient("Categories");
            await catTable.CreateIfNotExistsAsync();
            await foreach (var e in catTable.QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Name" }))
                catLookup[e.RowKey] = e.GetString("DisplayName") ?? e.GetString("Name") ?? e.RowKey;

            var userLookup = await _adminAuth.GetUserDisplayNameMapAsync();

            // Build guest token → nickname lookup
            var guestNickLookup = new Dictionary<string, string>();
            var guestTable = _tableService.GetTableClient("GuestTokens");
            try
            {
                await guestTable.CreateIfNotExistsAsync();
                await foreach (var e in guestTable.QueryAsync<TableEntity>(
                    filter: "PartitionKey eq 'guest'",
                    select: new[] { "Token", "NickName" }))
                {
                    var token = e.GetString("Token") ?? "";
                    var nick = e.GetString("NickName") ?? "";
                    if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(nick))
                        guestNickLookup[token] = nick;
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "GuestTokens table not available"); }

            var tableClient = _tableService.GetTableClient("GPSRecords");
            await tableClient.CreateIfNotExistsAsync();

            var lostDogFilter = req.Query["lostDog"].FirstOrDefault();
            var nameFilter = req.Query["name"].FirstOrDefault();
            var categoryFilterRaw = req.Query["category"].FirstOrDefault();
            var categoryFilters = string.IsNullOrEmpty(categoryFilterRaw)
                ? Array.Empty<string>()
                : categoryFilterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var pageSizeStr = req.Query["pageSize"].FirstOrDefault();
            var pageStr = req.Query["page"].FirstOrDefault();

            int? pageSize = pageSizeStr == "all" ? null : int.TryParse(pageSizeStr, out var ps) ? ps : 20;
            int page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;

            var allRecords = new List<object>();
            var allLostDogKeys = new HashSet<string>();
            var allNameKeys = new HashSet<string>();
            var allCategoryKeys = new HashSet<string>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                var lostDogKey = entity.GetString("LostDog") ?? "";
                var nameKey = entity.PartitionKey ?? "";
                var categoryKey = entity.GetString("Category") ?? "";

                // Filters work on RowKey (FK) values
                if (!string.IsNullOrEmpty(lostDogFilter) && lostDogKey != lostDogFilter)
                    continue;
                if (!string.IsNullOrEmpty(nameFilter) && nameKey != nameFilter)
                    continue;
                if (categoryFilters.Length > 0 && !categoryFilters.Contains(categoryKey))
                    continue;

                if (!string.IsNullOrEmpty(lostDogKey)) allLostDogKeys.Add(lostDogKey);
                if (!string.IsNullOrEmpty(nameKey)) allNameKeys.Add(nameKey);
                if (!string.IsNullOrEmpty(categoryKey)) allCategoryKeys.Add(categoryKey);

                // Resolve display name: for GUEST records use nickname from GuestToken
                string displayName;
                if (nameKey == "GUEST")
                {
                    var gToken = entity.GetString("GuestToken") ?? "";
                    displayName = !string.IsNullOrEmpty(gToken) && guestNickLookup.TryGetValue(gToken, out var nick)
                        ? $"Gast: {nick}"
                        : "Gast-Helfer*in";
                }
                else
                {
                    displayName = userLookup.GetValueOrDefault(nameKey, nameKey);
                }

                allRecords.Add(new
                {
                    partitionKey = entity.PartitionKey,
                    rowKey = entity.RowKey,
                    nameKey,
                    name = displayName,
                    lostDogKey,
                    lostDog = dogLookup.GetValueOrDefault(lostDogKey, lostDogKey),
                    latitude = GetDoubleSafe(entity, "Latitude"),
                    longitude = GetDoubleSafe(entity, "Longitude"),
                    accuracy = GetDoubleSafe(entity, "Accuracy"),
                    recordedAt = entity.GetString("RecordedAt") ?? entity.Timestamp?.ToString("o") ?? "",
                    photoUrl = entity.GetString("PhotoUrl") ?? "",
                    comment = entity.GetString("Comment") ?? "",
                    location = entity.GetString("Location") ?? "",
                    categoryKey,
                    category = catLookup.GetValueOrDefault(categoryKey, categoryKey)
                });
            }

            int totalCount = allRecords.Count;

            var deComparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);

            // Build filter dropdown options with rowKey + displayName
            var lostDogs = allLostDogKeys
                .Select(k => new { rowKey = k, displayName = dogLookup.GetValueOrDefault(k, k) })
                .OrderBy(x => x.displayName, deComparer).ToList();
            var names = allNameKeys
                .Select(k => new { rowKey = k, displayName = k == "GUEST" ? "Gast-Helfer*in" : userLookup.GetValueOrDefault(k, k) })
                .OrderBy(x => x.displayName, deComparer).ToList();
            var categories = allCategoryKeys
                .Select(k => new { rowKey = k, displayName = catLookup.GetValueOrDefault(k, k) })
                .OrderBy(x => x.displayName, deComparer).ToList();

            IEnumerable<object> pagedRecords;
            if (pageSize.HasValue)
                pagedRecords = allRecords.Skip((page - 1) * pageSize.Value).Take(pageSize.Value);
            else
                pagedRecords = allRecords;

            return new OkObjectResult(new
            {
                records = pagedRecords,
                totalCount,
                page,
                pageSize = pageSize ?? totalCount,
                totalPages = pageSize.HasValue ? (int)Math.Ceiling((double)totalCount / pageSize.Value) : 1,
                lostDogs,
                names,
                categories
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading GPS records");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteGPSRecords")]
    public async Task<IActionResult> DeleteGPSRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/gps-records/delete")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 2) == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<List<DeleteKey>>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || body.Count == 0)
                return new BadRequestObjectResult(new { error = "Keine Einträge zum Löschen" });

            var tableClient = _tableService.GetTableClient("GPSRecords");
            var container = _blobService.GetBlobContainerClient(PhotoContainer);
            int deleted = 0;

            foreach (var key in body)
            {
                try
                {
                    // Try to read entity first to get PhotoUrl
                    try
                    {
                        var entity = await tableClient.GetEntityAsync<TableEntity>(key.PartitionKey, key.RowKey);
                        var photoUrl = entity.Value.GetString("PhotoUrl");
                        if (!string.IsNullOrEmpty(photoUrl))
                        {
                            // Extract blob name from URL: {container}/{name}/{rowKey}.ext
                            var uri = new Uri(photoUrl);
                            var blobName = string.Join("/", uri.Segments.Skip(2)).TrimStart('/');
                            try { await container.DeleteBlobIfExistsAsync(blobName); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete blob {Blob}", blobName); }
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Entity lookup failed during batch delete"); }

                    await tableClient.DeleteEntityAsync(key.PartitionKey, key.RowKey);
                    deleted++;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("GPS record not found: {PK}/{RK}", key.PartitionKey, key.RowKey);
                }
            }

            _logger.LogInformation("Deleted {Count} GPS records", deleted);
            return new OkObjectResult(new { deleted, message = $"{deleted} Einträge gelöscht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting GPS records");
            return new StatusCodeResult(500);
        }
    }

    // ── Public endpoints (no admin token, name+lostDog required) ──

    [Function("GetMyRecords")]
    public async Task<IActionResult> GetMyRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "my-records")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var nameFilter = req.Query["name"].FirstOrDefault();
            var lostDogFilter = req.Query["lostDog"].FirstOrDefault();
            var guestTokenFilter = req.Query["guestToken"].FirstOrDefault() ?? "";
            var ownerKeyFilter = req.Query["ownerKey"].FirstOrDefault() ?? "";

            if (string.IsNullOrWhiteSpace(lostDogFilter))
                return new BadRequestObjectResult(new { error = "lostDog ist erforderlich" });

            // Build lookup maps for FK resolution
            var dogLookup = new Dictionary<string, string>();
            var dogTable = _tableService.GetTableClient("LostDogs");
            await dogTable.CreateIfNotExistsAsync();
            await foreach (var e in dogTable.QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Location" }))
                dogLookup[e.RowKey] = e.GetString("DisplayName") ?? e.GetString("Location") ?? e.RowKey;

            var catLookup = new Dictionary<string, string>();
            var catTable = _tableService.GetTableClient("Categories");
            await catTable.CreateIfNotExistsAsync();
            await foreach (var e in catTable.QueryAsync<TableEntity>(select: new[] { "RowKey", "DisplayName", "Name" }))
                catLookup[e.RowKey] = e.GetString("DisplayName") ?? e.GetString("Name") ?? e.RowKey;

            // Resolve user display names
            var userLookup = await _adminAuth.GetUserDisplayNameMapAsync();

            var tableClient = _tableService.GetTableClient("GPSRecords");
            await tableClient.CreateIfNotExistsAsync();

            var pageSizeStr = req.Query["pageSize"].FirstOrDefault();
            var pageStr = req.Query["page"].FirstOrDefault();
            int? pageSize = pageSizeStr == "all" ? null : int.TryParse(pageSizeStr, out var ps) ? ps : 20;
            int page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;

            // Build query: filter by PK if name provided, otherwise cross-partition
            string? tableFilter = null;
            if (!string.IsNullOrWhiteSpace(nameFilter))
            {
                // Whitelist: only allow alphanumeric, hyphens, underscores, asterisks, max 64 chars
                if (nameFilter.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(nameFilter, @"^[a-zA-Z0-9\-_\*]+$"))
                    return new BadRequestObjectResult(new { error = "Ung\u00fcltiger Name-Filter" });
                tableFilter = $"PartitionKey eq '{nameFilter}'";
            }
            var allRecords = new List<object>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: tableFilter))
            {
                var lostDogKey = entity.GetString("LostDog") ?? "";
                if (lostDogKey != lostDogFilter) continue;

                var categoryKey = entity.GetString("Category") ?? "";
                var recordToken = entity.GetString("GuestToken") ?? "";
                var recordOwnerKey = entity.GetString("OwnerKey") ?? "";
                var isOwner = (!string.IsNullOrEmpty(guestTokenFilter)
                    && !string.IsNullOrEmpty(recordToken)
                    && recordToken == guestTokenFilter)
                    || (!string.IsNullOrEmpty(ownerKeyFilter)
                    && !string.IsNullOrEmpty(recordOwnerKey)
                    && recordOwnerKey == ownerKeyFilter);
                allRecords.Add(new
                {
                    partitionKey = entity.PartitionKey,
                    rowKey = entity.RowKey,
                    name = userLookup.GetValueOrDefault(entity.PartitionKey, entity.PartitionKey),
                    lostDogKey,
                    lostDog = dogLookup.GetValueOrDefault(lostDogKey, lostDogKey),
                    latitude = GetDoubleSafe(entity, "Latitude"),
                    longitude = GetDoubleSafe(entity, "Longitude"),
                    accuracy = GetDoubleSafe(entity, "Accuracy"),
                    recordedAt = entity.GetString("RecordedAt") ?? entity.Timestamp?.ToString("o") ?? "",
                    photoUrl = entity.GetString("PhotoUrl") ?? "",
                    comment = entity.GetString("Comment") ?? "",
                    categoryKey,
                    category = catLookup.GetValueOrDefault(categoryKey, categoryKey),
                    isOwner
                });
            }

            int totalCount = allRecords.Count;

            IEnumerable<object> pagedRecords;
            if (pageSize.HasValue)
                pagedRecords = allRecords.Skip((page - 1) * pageSize.Value).Take(pageSize.Value);
            else
                pagedRecords = allRecords;

            return new OkObjectResult(new
            {
                records = pagedRecords,
                totalCount,
                page,
                pageSize = pageSize ?? totalCount,
                totalPages = pageSize.HasValue ? (int)Math.Ceiling((double)totalCount / pageSize.Value) : 1
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading user GPS records");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteMyRecords")]
    public async Task<IActionResult> DeleteMyRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "my-records/delete")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var body = await JsonSerializer.DeserializeAsync<MyDeleteRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.LostDog))
                return new BadRequestObjectResult(new { error = "lostDog und keys sind erforderlich" });

            if (body.Keys is null || body.Keys.Count == 0)
                return new BadRequestObjectResult(new { error = "Keine Einträge zum Löschen" });

            var tableClient = _tableService.GetTableClient("GPSRecords");
            var container = _blobService.GetBlobContainerClient(PhotoContainer);
            int deleted = 0;

            foreach (var key in body.Keys)
            {
                try
                {
                    var entity = await tableClient.GetEntityAsync<TableEntity>(key.PartitionKey, key.RowKey);
                    var entityDog = entity.Value.GetString("LostDog") ?? "";
                    if (entityDog != body.LostDog) continue; // dog ownership check

                    // Guest token ownership: if guestToken is provided, only delete records with matching token
                    if (!string.IsNullOrEmpty(body.GuestToken))
                    {
                        var recordToken = entity.Value.GetString("GuestToken") ?? "";
                        if (recordToken != body.GuestToken) continue;
                    }

                    // Owner key ownership: if ownerKey is provided, only delete records with matching key
                    if (!string.IsNullOrEmpty(body.OwnerKey))
                    {
                        var recordOwnerKey = entity.Value.GetString("OwnerKey") ?? "";
                        if (recordOwnerKey != body.OwnerKey) continue;
                    }

                    var photoUrl = entity.Value.GetString("PhotoUrl");
                    if (!string.IsNullOrEmpty(photoUrl))
                    {
                        var uri = new Uri(photoUrl);
                        var blobName = string.Join("/", uri.Segments.Skip(2)).TrimStart('/');
                        try { await container.DeleteBlobIfExistsAsync(blobName); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Failed to delete photo blob"); }
                    }
                    await tableClient.DeleteEntityAsync(key.PartitionKey, key.RowKey);
                    deleted++;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }
            }

            _logger.LogInformation("User deleted {Count} GPS records (name={Name}, dog={Dog})", deleted, body.Name, body.LostDog);
            return new OkObjectResult(new { deleted, message = $"{deleted} Einträge gelöscht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user GPS records");
            return new StatusCodeResult(500);
        }
    }

    private record DeleteKey
    {
        public string PartitionKey { get; init; } = "";
        public string RowKey { get; init; } = "";
    }

    private record MyDeleteRequest
    {
        public string Name { get; init; } = "";
        public string LostDog { get; init; } = "";
        public string? GuestToken { get; init; }
        public string? OwnerKey { get; init; }
        public List<DeleteKey> Keys { get; init; } = new();
    }

    private record UpdateRequest
    {
        public List<UpdateKey> Keys { get; init; } = new();
        public string? Name { get; init; }
        public string? LostDog { get; init; }
        public string? Category { get; init; }
        public string? Comment { get; init; }
        public string? Location { get; init; }
        public string? RecordedAt { get; init; }
        public bool DeletePhoto { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
    }

    private record UpdateKey
    {
        public string PartitionKey { get; init; } = "";
        public string RowKey { get; init; } = "";
    }

    [Function("UpdateGPSRecords")]
    public async Task<IActionResult> UpdateGPSRecords(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/gps-records/update")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 2) == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<UpdateRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || body.Keys is null || body.Keys.Count == 0)
                return new BadRequestObjectResult(new { error = "Keine Einträge zum Bearbeiten" });

            var tableClient = _tableService.GetTableClient("GPSRecords");
            var container = _blobService.GetBlobContainerClient(PhotoContainer);
            int updated = 0;

            foreach (var key in body.Keys)
            {
                try
                {
                    var response = await tableClient.GetEntityAsync<TableEntity>(key.PartitionKey, key.RowKey);
                    var entity = response.Value;

                    bool nameChanged = !string.IsNullOrEmpty(body.Name) && body.Name != entity.PartitionKey;

                    // Update fields (only if provided)
                    if (!string.IsNullOrEmpty(body.LostDog))
                        entity["LostDog"] = body.LostDog;
                    if (body.Category is not null) // allow empty string to clear
                        entity["Category"] = body.Category;
                    if (body.Comment is not null) // allow empty string to clear
                    {
                        var sanitized = InputSanitizer.StripHtml(body.Comment);
                        entity["Comment"] = sanitized.Length > 40 ? sanitized[..40] : sanitized;
                    }
                    if (body.Location is not null)
                        entity["Location"] = InputSanitizer.StripHtml(body.Location);
                    if (!string.IsNullOrEmpty(body.RecordedAt))
                        entity["RecordedAt"] = body.RecordedAt;
                    if (body.Latitude.HasValue)
                        entity["Latitude"] = body.Latitude.Value;
                    if (body.Longitude.HasValue)
                        entity["Longitude"] = body.Longitude.Value;

                    // Delete photo if requested
                    if (body.DeletePhoto)
                    {
                        var photoUrl = entity.GetString("PhotoUrl");
                        if (!string.IsNullOrEmpty(photoUrl))
                        {
                            try
                            {
                                var uri = new Uri(photoUrl);
                                var blobName = string.Join("/", uri.Segments.Skip(2)).TrimStart('/');
                                await container.DeleteBlobIfExistsAsync(blobName);
                            }
                            catch { /* best effort */ }
                        }
                        entity["PhotoUrl"] = "";
                    }

                    if (nameChanged)
                    {
                        // PartitionKey changed → create new entity, delete old
                        var newEntity = new TableEntity(body.Name, entity.RowKey);
                        foreach (var prop in entity)
                        {
                            if (prop.Key is "odata.etag" or "PartitionKey" or "RowKey" or "Timestamp")
                                continue;
                            newEntity[prop.Key] = prop.Value;
                        }
                        await tableClient.AddEntityAsync(newEntity);
                        await tableClient.DeleteEntityAsync(key.PartitionKey, key.RowKey);
                    }
                    else
                    {
                        await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                    }

                    updated++;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    _logger.LogWarning("GPS record not found for update: {PK}/{RK}", key.PartitionKey, key.RowKey);
                }
            }

            _logger.LogInformation("Updated {Count} GPS records", updated);
            return new OkObjectResult(new { updated, message = $"{updated} Einträge aktualisiert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating GPS records");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Safely read a double from a TableEntity, handling Int64/Int32 values from import.</summary>
    private static double GetDoubleSafe(TableEntity entity, string key)
    {
        var val = entity[key];
        return val switch
        {
            double d => d,
            long l => l,
            int i => i,
            _ => 0
        };
    }
}
