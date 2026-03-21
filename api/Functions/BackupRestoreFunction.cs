using System.Text.Json;
using Azure.Data.Tables;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class BackupRestoreFunction
{
    private readonly TableServiceClient _tableService;
    private readonly ILogger<BackupRestoreFunction> _logger;
    private readonly ApiKeyValidator _apiKey;
    private readonly AdminAuth _adminAuth;
    private readonly RateLimitProvider _rateLimit;

    private static readonly string[] BackupTables = { "GPSRecords", "Users", "LostDogs", "Categories", "Config" };

    public BackupRestoreFunction(TableServiceClient tableService,
        ILogger<BackupRestoreFunction> logger, ApiKeyValidator apiKey,
        AdminAuth adminAuth, RateLimitProvider rateLimit)
    {
        _tableService = tableService;
        _logger = logger;
        _apiKey = apiKey;
        _adminAuth = adminAuth;
        _rateLimit = rateLimit;
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
