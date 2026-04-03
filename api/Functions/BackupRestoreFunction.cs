using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class BackupRestoreFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<BackupRestoreFunction> _logger;

    public BackupRestoreFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<BackupRestoreFunction> logger)
    {
        _auth = auth; _apiKey = apiKey; _rateLimiter = rateLimiter; _tables = tables; _logger = logger;
    }

    [Function("BackupExport")]
    public async Task<HttpResponseData> Export(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backup")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Read); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "maintenance.admin"); if (ctx is null) return pe!;
        try
        {
            var backup = new Dictionary<string, List<Dictionary<string, object?>>>();
            foreach (var tableName in TenantTableFactory.TableNames)
            {
                var table = _tables.GetTableClient(ctx.TenantId, tableName);
                var rows = new List<Dictionary<string, object?>>();
                try
                {
                    await foreach (var entity in table.QueryAsync<TableEntity>())
                    {
                        var row = new Dictionary<string, object?> { ["PartitionKey"] = entity.PartitionKey, ["RowKey"] = entity.RowKey, ["Timestamp"] = entity.Timestamp?.ToString("o") };
                        foreach (var prop in entity.Keys)
                        {
                            if (prop is "odata.etag" or "PartitionKey" or "RowKey" or "Timestamp") continue;
                            row[prop] = entity[prop];
                        }
                        rows.Add(row);
                    }
                }
                catch { /* table might not exist yet */ }
                backup[tableName] = rows;
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { exportedAt = DateTimeOffset.UtcNow.ToString("o"), version = "2.0", tenantId = ctx.TenantId, tables = backup });
        }
        catch (Exception ex) { _logger.LogError(ex, "Backup export failed"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Backup fehlgeschlagen."); }
    }

    [Function("BackupRestore")]
    public async Task<HttpResponseData> Restore(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/restore")] HttpRequestData req)
    {
        var e = req.ValidateApiKey(_apiKey); if (e is not null) return e;
        e = req.CheckRateLimit(_rateLimiter.Write); if (e is not null) return e;
        var (ctx, _, pe) = await req.RequirePermissionAsync(_auth, "maintenance.admin"); if (ctx is null) return pe!;
        try
        {
            var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tables", out var tablesEl))
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültiges Backup-Format: 'tables' fehlt.");
            var stats = new Dictionary<string, int>();
            foreach (var tableName in TenantTableFactory.TableNames)
            {
                if (!tablesEl.TryGetProperty(tableName, out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array) continue;
                var table = _tables.GetTableClient(ctx.TenantId, tableName);
                int count = 0;
                foreach (var rowEl in rowsEl.EnumerateArray())
                {
                    var pk = rowEl.GetProperty("PartitionKey").GetString();
                    var rk = rowEl.GetProperty("RowKey").GetString();
                    if (string.IsNullOrEmpty(pk) || string.IsNullOrEmpty(rk)) continue;
                    var entity = new TableEntity(pk, rk);
                    foreach (var prop in rowEl.EnumerateObject())
                    {
                        if (prop.Name is "PartitionKey" or "RowKey" or "Timestamp") continue;
                        switch (prop.Value.ValueKind)
                        {
                            case JsonValueKind.String: entity[prop.Name] = prop.Value.GetString(); break;
                            case JsonValueKind.Number:
                                if (prop.Name is "Latitude" or "Longitude" or "Accuracy") entity[prop.Name] = prop.Value.GetDouble();
                                else if (prop.Value.TryGetInt64(out var l)) entity[prop.Name] = l;
                                else entity[prop.Name] = prop.Value.GetDouble();
                                break;
                            case JsonValueKind.True: case JsonValueKind.False: entity[prop.Name] = prop.Value.GetBoolean(); break;
                        }
                    }
                    await table.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                    count++;
                }
                stats[tableName] = count;
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Wiederherstellung abgeschlossen.", restored = stats });
        }
        catch (JsonException) { return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültiges JSON-Format."); }
        catch (Exception ex) { _logger.LogError(ex, "Backup restore failed"); return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Wiederherstellung fehlgeschlagen."); }
    }
}

    /// <summary>Export all table data as JSON.</summary>
    [Function("BackupExport")]
    public async Task<IActionResult> Export(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backup")] HttpRequest req)
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

            var backup = new Dictionary<string, List<Dictionary<string, object?>>>();

            foreach (var tableName in BackupTables)
            {
                var tableClient = _tableService.GetTableClient(tableName);
                try { await tableClient.CreateIfNotExistsAsync(); } catch { continue; }

                var rows = new List<Dictionary<string, object?>>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>())
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["PartitionKey"] = entity.PartitionKey,
                        ["RowKey"] = entity.RowKey,
                        ["Timestamp"] = entity.Timestamp?.ToString("o")
                    };
                    foreach (var prop in entity.Keys)
                    {
                        if (prop is "odata.etag" or "PartitionKey" or "RowKey" or "Timestamp") continue;
                        row[prop] = entity[prop];
                    }
                    rows.Add(row);
                }
                backup[tableName] = rows;
            }

            var result = new
            {
                exportedAt = DateTimeOffset.UtcNow.ToString("o"),
                version = "1.0",
                tables = backup
            };

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup export failed");
            return new ObjectResult(new { error = "Backup fehlgeschlagen" }) { StatusCode = 500 };
        }
    }

    /// <summary>Import (restore) table data from JSON. Strategy: upsert (merge).</summary>
    [Function("BackupRestore")]
    public async Task<IActionResult> Restore(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/restore")] HttpRequest req)
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

            var doc = await JsonDocument.ParseAsync(req.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tables", out var tablesEl))
                return new BadRequestObjectResult(new { error = "Ungültiges Backup-Format: 'tables' fehlt" });

            var stats = new Dictionary<string, int>();

            foreach (var tableName in BackupTables)
            {
                if (!tablesEl.TryGetProperty(tableName, out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
                    continue;

                var tableClient = _tableService.GetTableClient(tableName);
                await tableClient.CreateIfNotExistsAsync();

                int count = 0;
                foreach (var rowEl in rowsEl.EnumerateArray())
                {
                    var pk = rowEl.GetProperty("PartitionKey").GetString();
                    var rk = rowEl.GetProperty("RowKey").GetString();
                    if (string.IsNullOrEmpty(pk) || string.IsNullOrEmpty(rk)) continue;

                    var entity = new TableEntity(pk, rk);
                    foreach (var prop in rowEl.EnumerateObject())
                    {
                        if (prop.Name is "PartitionKey" or "RowKey" or "Timestamp") continue;

                        switch (prop.Value.ValueKind)
                        {
                            case JsonValueKind.String:
                                entity[prop.Name] = prop.Value.GetString();
                                break;
                            case JsonValueKind.Number:
                                // Always store numeric fields that represent coordinates as Double
                                if (prop.Name is "Latitude" or "Longitude" or "Accuracy")
                                    entity[prop.Name] = prop.Value.GetDouble();
                                else if (prop.Value.TryGetInt64(out var l))
                                    entity[prop.Name] = l;
                                else
                                    entity[prop.Name] = prop.Value.GetDouble();
                                break;
                            case JsonValueKind.True:
                            case JsonValueKind.False:
                                entity[prop.Name] = prop.Value.GetBoolean();
                                break;
                        }
                    }

                    await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                    count++;
                }
                stats[tableName] = count;
            }

            return new OkObjectResult(new { message = "Wiederherstellung abgeschlossen", restored = stats });
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new { error = "Ungültiges JSON-Format" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup restore failed");
            return new ObjectResult(new { error = "Wiederherstellung fehlgeschlagen" }) { StatusCode = 500 };
        }
    }
}
