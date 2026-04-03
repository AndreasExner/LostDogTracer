using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Azure.Data.Tables;

namespace LostDogTracer.Api.Helpers;

/// <summary>
/// Central factory for tenant-scoped Azure Table clients.
/// Each tenant gets its own set of tables: {tenantId}{TableName}.
/// TableClients are cached per (tenantId, tableName) pair.
/// </summary>
public partial class TenantTableFactory
{
    /// <summary>Known table names for a tenant.</summary>
    public static readonly string[] TableNames =
    {
        "Users", "Roles", "GPSRecords", "LostDogs", "Categories",
        "Equipment", "GuestTokens", "Config", "Deployments"
    };

    private readonly TableServiceClient _tableService;
    private readonly ConcurrentDictionary<string, TableClient> _cache = new();

    public TenantTableFactory(TableServiceClient tableService)
    {
        _tableService = tableService;
    }

    /// <summary>
    /// Get a TableClient for the given tenant and table.
    /// Returns a cached instance; does NOT create the table automatically.
    /// </summary>
    public TableClient GetTableClient(string tenantId, string tableName)
    {
        ValidateTenantId(tenantId);
        var fullName = $"{tenantId}{tableName}";
        return _cache.GetOrAdd(fullName, name => _tableService.GetTableClient(name));
    }

    /// <summary>
    /// Ensure all 9 tables exist for a tenant. Idempotent — safe to call repeatedly.
    /// </summary>
    public async Task EnsureTablesExistAsync(string tenantId)
    {
        ValidateTenantId(tenantId);
        foreach (var name in TableNames)
        {
            var client = GetTableClient(tenantId, name);
            await client.CreateIfNotExistsAsync();
        }
    }

    /// <summary>
    /// Validate that tenantId is alphanumeric and 1-20 characters.
    /// Azure Table names must be 3-63 chars, start with letter, alphanumeric only.
    /// With table suffix (e.g. "Users" = 5 chars), tenantId max is ~58 chars,
    /// but we limit to 20 for readability.
    /// </summary>
    private static void ValidateTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("Tenant ID must not be empty.", nameof(tenantId));

        if (tenantId.Length > 20)
            throw new ArgumentException("Tenant ID must be 20 characters or less.", nameof(tenantId));

        if (!TenantIdRegex().IsMatch(tenantId))
            throw new ArgumentException("Tenant ID must be alphanumeric only.", nameof(tenantId));
    }

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9]*$")]
    private static partial Regex TenantIdRegex();
}
