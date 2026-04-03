using System.Net;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class CategoriesFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<CategoriesFunction> _logger;

    public CategoriesFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<CategoriesFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _logger = logger;
    }

    [Function("GetCategories")]
    public async Task<HttpResponseData> GetCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "categories")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");
        try
        {
            var table = _tables.GetTableClient(tenantId, "Categories");
            var items = new List<(string rk, string name, string svg)>();
            await foreach (var ent in table.QueryAsync<TableEntity>())
            {
                var n = ent.GetString("DisplayName") ?? ent.GetString("Name") ?? ent.RowKey;
                if (!string.IsNullOrWhiteSpace(n)) items.Add((ent.RowKey, n, ent.GetString("SvgSymbol") ?? ""));
            }
            var c = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => c.Compare(a.name, b.name));
            return req.CreateJsonResponse(HttpStatusCode.OK, items.Select(i => new { rowKey = i.rk, displayName = i.name, svgSymbol = i.svg }));
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading categories"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("GetCategoriesAdmin")]
    public async Task<HttpResponseData> GetCategoriesAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/categories")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "categories.write"); if (ctx is null) return pe!;
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Categories");
            var items = new List<(string pk, string rk, string name, string svg)>();
            await foreach (var ent in table.QueryAsync<TableEntity>())
                items.Add((ent.PartitionKey, ent.RowKey, ent.GetString("DisplayName") ?? ent.GetString("Name") ?? ent.RowKey, ent.GetString("SvgSymbol") ?? ""));
            var c = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => c.Compare(a.name, b.name));
            return req.CreateJsonResponse(HttpStatusCode.OK, items.Select(i => new { partitionKey = i.pk, rowKey = i.rk, displayName = i.name, svgSymbol = i.svg }));
        }
        catch (Exception ex) { _logger.LogError(ex, "Error loading categories (admin)"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("CreateCategory")]
    public async Task<HttpResponseData> CreateCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/categories")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "categories.write"); if (ctx is null) return pe!;
        var (body, be) = await req.ReadJsonBodyAsync<CreateCategoryRequest>(); if (body is null) return be!;
        if (string.IsNullOrWhiteSpace(body.Name)) return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Name darf nicht leer sein.");
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Categories");
            var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
            var entity = new TableEntity("categories", rowKey) { { "DisplayName", body.Name.Trim() }, { "SvgSymbol", body.SvgSymbol ?? "" } };
            await table.AddEntityAsync(entity);
            return req.CreateJsonResponse(HttpStatusCode.Created, new { partitionKey = "categories", rowKey, displayName = body.Name.Trim(), svgSymbol = body.SvgSymbol ?? "" });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error creating category"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("UpdateCategory")]
    public async Task<HttpResponseData> UpdateCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/categories/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "categories.write"); if (ctx is null) return pe!;
        var (body, be) = await req.ReadJsonBodyAsync<UpdateCategoryRequest>(); if (body is null) return be!;
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Categories");
            var entity = (await table.GetEntityAsync<TableEntity>("categories", rowKey)).Value;
            if (!string.IsNullOrWhiteSpace(body.Name)) entity["DisplayName"] = body.Name.Trim();
            if (body.SvgSymbol is not null) entity["SvgSymbol"] = body.SvgSymbol;
            await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { return req.CreateErrorResponse(HttpStatusCode.NotFound, "Kategorie nicht gefunden."); }
        catch (Exception ex) { _logger.LogError(ex, "Error updating category"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("DeleteCategory")]
    public async Task<HttpResponseData> DeleteCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/categories/{rowKey}")] HttpRequestData req, string rowKey)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "categories.write"); if (ctx is null) return pe!;
        try
        {
            _tables.GetTableClient(ctx.TenantId, "Categories");
            await _tables.GetTableClient(ctx.TenantId, "Categories").DeleteEntityAsync("categories", rowKey);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Gelöscht." });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error deleting category"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("SeedCategories")]
    public async Task<HttpResponseData> SeedCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/categories/seed")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "categories.write"); if (ctx is null) return pe!;
        try
        {
            var table = _tables.GetTableClient(ctx.TenantId, "Categories");
            bool hasData = false;
            await foreach (var _ in table.QueryAsync<TableEntity>(maxPerPage: 1)) { hasData = true; break; }
            if (hasData) return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Kategorien existieren bereits", seeded = 0 });

            var defaults = new Dictionary<string, string>
            {
                ["Flyer/Handzettel"] = @"<rect x=""7"" y=""5"" width=""10"" height=""13"" rx=""1"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><line x1=""9.5"" y1=""9"" x2=""14.5"" y2=""9"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""12"" x2=""14.5"" y2=""12"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""15"" x2=""12.5"" y2=""15"" stroke=""#fff"" stroke-width=""1.2""/>",
                ["Sichtung"] = @"<ellipse cx=""12"" cy=""12"" rx=""6"" ry=""4"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""2"" fill=""#fff""/>",
                ["Entlaufort"] = @"<circle cx=""9"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""15"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""7"" cy=""13"" r=""1.3"" fill=""#fff""/><circle cx=""17"" cy=""13"" r=""1.3"" fill=""#fff""/><ellipse cx=""12"" cy=""15"" rx=""3"" ry=""2.2"" fill=""#fff""/>",
                ["Standort Falle"] = @"<circle cx=""12"" cy=""12"" r=""5"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""1.5"" fill=""#fff""/><line x1=""12"" y1=""5"" x2=""12"" y2=""8"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""12"" y1=""16"" x2=""12"" y2=""19"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""5"" y1=""12"" x2=""8"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""16"" y1=""12"" x2=""19"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/>"
            };
            int count = 0;
            foreach (var (name, svg) in defaults)
            {
                var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
                await Task.Delay(5);
                await table.AddEntityAsync(new TableEntity("categories", rowKey) { { "DisplayName", name }, { "SvgSymbol", svg } });
                count++;
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = $"{count} Kategorien angelegt", seeded = count });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error seeding categories"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    [Function("BackfillCategorySvg")]
    public async Task<HttpResponseData> BackfillCategorySvg(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/categories/backfill-svg")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "categories.write"); if (ctx is null) return pe!;
        try
        {
            var defaults = new Dictionary<string, string>
            {
                ["Flyer/Handzettel"] = @"<rect x=""7"" y=""5"" width=""10"" height=""13"" rx=""1"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><line x1=""9.5"" y1=""9"" x2=""14.5"" y2=""9"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""12"" x2=""14.5"" y2=""12"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""15"" x2=""12.5"" y2=""15"" stroke=""#fff"" stroke-width=""1.2""/>",
                ["Sichtung"] = @"<ellipse cx=""12"" cy=""12"" rx=""6"" ry=""4"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""2"" fill=""#fff""/>",
                ["Entlaufort"] = @"<circle cx=""9"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""15"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""7"" cy=""13"" r=""1.3"" fill=""#fff""/><circle cx=""17"" cy=""13"" r=""1.3"" fill=""#fff""/><ellipse cx=""12"" cy=""15"" rx=""3"" ry=""2.2"" fill=""#fff""/>",
                ["Standort Falle"] = @"<circle cx=""12"" cy=""12"" r=""5"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""1.5"" fill=""#fff""/><line x1=""12"" y1=""5"" x2=""12"" y2=""8"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""12"" y1=""16"" x2=""12"" y2=""19"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""5"" y1=""12"" x2=""8"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""16"" y1=""12"" x2=""19"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/>"
            };
            var table = _tables.GetTableClient(ctx.TenantId, "Categories");
            int updated = 0;
            await foreach (var entity in table.QueryAsync<TableEntity>())
            {
                if (!string.IsNullOrEmpty(entity.GetString("SvgSymbol"))) continue;
                var dn = entity.GetString("DisplayName") ?? entity.GetString("Name") ?? "";
                if (defaults.TryGetValue(dn, out var svg))
                {
                    entity["SvgSymbol"] = svg;
                    await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
                    updated++;
                }
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = $"{updated} Kategorien aktualisiert", updated });
        }
        catch (Exception ex) { _logger.LogError(ex, "Error backfilling SVG"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler."); }
    }

    private record CreateCategoryRequest(string? Name, string? SvgSymbol);
    private record UpdateCategoryRequest(string? Name, string? SvgSymbol);
}
    public async Task<IActionResult> GetCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "categories")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var tableClient = _tableService.GetTableClient("Categories");
            await tableClient.CreateIfNotExistsAsync();

            var categories = new List<(string rowKey, string name, string svgSymbol)>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                var name = entity.GetString("DisplayName") ?? entity.GetString("Name") ?? entity.RowKey;
                if (!string.IsNullOrWhiteSpace(name))
                    categories.Add((entity.RowKey, name, entity.GetString("SvgSymbol") ?? ""));
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            categories.Sort((a, b) => comparer.Compare(a.name, b.name));
            return new OkObjectResult(categories.Select(c => new { rowKey = c.rowKey, displayName = c.name, c.svgSymbol }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading categories");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Admin endpoint – returns list with keys for management.</summary>
    [Function("GetCategoriesAdmin")]
    public async Task<IActionResult> GetCategoriesAdmin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/categories")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var tableClient = _tableService.GetTableClient("Categories");
            await tableClient.CreateIfNotExistsAsync();

            var items = new List<(string partitionKey, string rowKey, string name, string svgSymbol)>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                items.Add((
                    entity.PartitionKey,
                    entity.RowKey,
                    entity.GetString("DisplayName") ?? entity.GetString("Name") ?? entity.RowKey,
                    entity.GetString("SvgSymbol") ?? ""
                ));
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.name, b.name));

            return new OkObjectResult(items.Select(i => new { i.partitionKey, i.rowKey, displayName = i.name, i.svgSymbol }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading categories (admin)");
            return new StatusCodeResult(500);
        }
    }

    [Function("CreateCategory")]
    public async Task<IActionResult> CreateCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/categories")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var name = body.GetProperty("name").GetString();

            if (string.IsNullOrWhiteSpace(name))
                return new BadRequestObjectResult(new { error = "Name darf nicht leer sein" });

            var tableClient = _tableService.GetTableClient("Categories");
            await tableClient.CreateIfNotExistsAsync();

            var svgSymbol = "";
            if (body.TryGetProperty("svgSymbol", out var svgProp))
                svgSymbol = svgProp.GetString() ?? "";

            var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
            var entity = new TableEntity("categories", rowKey)
            {
                { "DisplayName", name.Trim() },
                { "SvgSymbol", svgSymbol }
            };

            await tableClient.AddEntityAsync(entity);
            _logger.LogInformation("Category created: {Name}", name);

            return new CreatedResult("", new { partitionKey = "categories", rowKey, displayName = name.Trim(), svgSymbol });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating category");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteCategory")]
    public async Task<IActionResult> DeleteCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/categories/{rowKey}")] HttpRequest req,
        string rowKey)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var tableClient = _tableService.GetTableClient("Categories");
            await tableClient.DeleteEntityAsync("categories", rowKey);
            _logger.LogInformation("Category deleted: RowKey={RowKey}", rowKey);

            return new OkObjectResult(new { message = "Gelöscht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting category");
            return new StatusCodeResult(500);
        }
    }

    [Function("UpdateCategory")]
    public async Task<IActionResult> UpdateCategory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/categories/{rowKey}")] HttpRequest req,
        string rowKey)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var tableClient = _tableService.GetTableClient("Categories");

            var entity = await tableClient.GetEntityAsync<TableEntity>("categories", rowKey);
            if (body.TryGetProperty("name", out var nameProp))
            {
                var n = nameProp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(n)) entity.Value["DisplayName"] = n;
            }
            if (body.TryGetProperty("svgSymbol", out var svgProp))
                entity.Value["SvgSymbol"] = svgProp.GetString() ?? "";

            await tableClient.UpdateEntityAsync(entity.Value, entity.Value.ETag, TableUpdateMode.Replace);
            _logger.LogInformation("Category updated: RowKey={RowKey}", rowKey);

            return new OkObjectResult(new { message = "Aktualisiert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating category");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Seeds initial categories if the table is empty.</summary>
    [Function("SeedCategories")]
    public async Task<IActionResult> SeedCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/categories/seed")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var tableClient = _tableService.GetTableClient("Categories");
            await tableClient.CreateIfNotExistsAsync();

            // Check if already populated
            var existing = new List<TableEntity>();
            await foreach (var e in tableClient.QueryAsync<TableEntity>(maxPerPage: 1))
            {
                existing.Add(e);
                break;
            }

            if (existing.Count > 0)
                return new OkObjectResult(new { message = "Kategorien existieren bereits", seeded = 0 });

            var defaults = new Dictionary<string, string>
            {
                ["Flyer/Handzettel"] = @"<rect x=""7"" y=""5"" width=""10"" height=""13"" rx=""1"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><line x1=""9.5"" y1=""9"" x2=""14.5"" y2=""9"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""12"" x2=""14.5"" y2=""12"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""15"" x2=""12.5"" y2=""15"" stroke=""#fff"" stroke-width=""1.2""/>",
                ["Sichtung"] = @"<ellipse cx=""12"" cy=""12"" rx=""6"" ry=""4"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""2"" fill=""#fff""/>",
                ["Entlaufort"] = @"<circle cx=""9"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""15"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""7"" cy=""13"" r=""1.3"" fill=""#fff""/><circle cx=""17"" cy=""13"" r=""1.3"" fill=""#fff""/><ellipse cx=""12"" cy=""15"" rx=""3"" ry=""2.2"" fill=""#fff""/>",
                ["Standort Falle"] = @"<circle cx=""12"" cy=""12"" r=""5"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""1.5"" fill=""#fff""/><line x1=""12"" y1=""5"" x2=""12"" y2=""8"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""12"" y1=""16"" x2=""12"" y2=""19"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""5"" y1=""12"" x2=""8"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""16"" y1=""12"" x2=""19"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/>"
            };
            int count = 0;
            foreach (var (name, svg) in defaults)
            {
                var rowKey = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15");
                await Task.Delay(5); // ensure unique rowKeys
                var entity = new TableEntity("categories", rowKey)
                {
                    { "DisplayName", name },
                    { "SvgSymbol", svg }
                };
                await tableClient.AddEntityAsync(entity);
                count++;
            }

            _logger.LogInformation("Seeded {Count} default categories", count);
            return new OkObjectResult(new { message = $"{count} Kategorien angelegt", seeded = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding categories");
            return new StatusCodeResult(500);
        }
    }

    /// <summary>Backfills SvgSymbol on existing categories that don't have one yet.</summary>
    [Function("BackfillCategorySvg")]
    public async Task<IActionResult> BackfillCategorySvg(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/categories/backfill-svg")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _adminAuth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var defaults = new Dictionary<string, string>
            {
                ["Flyer/Handzettel"] = @"<rect x=""7"" y=""5"" width=""10"" height=""13"" rx=""1"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><line x1=""9.5"" y1=""9"" x2=""14.5"" y2=""9"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""12"" x2=""14.5"" y2=""12"" stroke=""#fff"" stroke-width=""1.2""/><line x1=""9.5"" y1=""15"" x2=""12.5"" y2=""15"" stroke=""#fff"" stroke-width=""1.2""/>",
                ["Sichtung"] = @"<ellipse cx=""12"" cy=""12"" rx=""6"" ry=""4"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""2"" fill=""#fff""/>",
                ["Entlaufort"] = @"<circle cx=""9"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""15"" cy=""9"" r=""1.5"" fill=""#fff""/><circle cx=""7"" cy=""13"" r=""1.3"" fill=""#fff""/><circle cx=""17"" cy=""13"" r=""1.3"" fill=""#fff""/><ellipse cx=""12"" cy=""15"" rx=""3"" ry=""2.2"" fill=""#fff""/>",
                ["Standort Falle"] = @"<circle cx=""12"" cy=""12"" r=""5"" fill=""none"" stroke=""#fff"" stroke-width=""1.5""/><circle cx=""12"" cy=""12"" r=""1.5"" fill=""#fff""/><line x1=""12"" y1=""5"" x2=""12"" y2=""8"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""12"" y1=""16"" x2=""12"" y2=""19"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""5"" y1=""12"" x2=""8"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/><line x1=""16"" y1=""12"" x2=""19"" y2=""12"" stroke=""#fff"" stroke-width=""1.3""/>"
            };

            var tableClient = _tableService.GetTableClient("Categories");
            int updated = 0;

            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                var name = entity.GetString("DisplayName") ?? entity.GetString("Name") ?? "";
                var existing = entity.GetString("SvgSymbol") ?? "";
                if (string.IsNullOrWhiteSpace(existing) && defaults.TryGetValue(name, out var svg))
                {
                    entity["SvgSymbol"] = svg;
                    await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                    updated++;
                }
            }

            return new OkObjectResult(new { message = $"{updated} Kategorien aktualisiert", updated });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error backfilling category SVGs");
            return new StatusCodeResult(500);
        }
    }
}
