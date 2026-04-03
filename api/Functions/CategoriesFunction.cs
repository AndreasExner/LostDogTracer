using System.Text.Json;
using Azure.Data.Tables;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class CategoriesFunction
{
    private readonly TableServiceClient _tableService;
    private readonly ILogger<CategoriesFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;
    private readonly RateLimitProvider _rateLimit;

    public CategoriesFunction(TableServiceClient tableService, ILogger<CategoriesFunction> logger,
        ApiKeyValidator apiKey, AdminAuth adminAuth, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
        _rateLimit = rateLimit;
    }

    /// <summary>Public endpoint – returns sorted list of category names (for the submit form).</summary>
    [Function("GetCategories")]
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
