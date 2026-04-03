using System.Text.Json;
using Azure.Data.Tables;
using LostDogTracer.Api.Helpers;

namespace LostDogTracer.Api.Security;

/// <summary>
/// RBAC permission checker. Loads user → role → permissions from tenant tables.
/// Caches role permissions in memory to avoid repeated table lookups.
/// </summary>
public class PermissionChecker
{
    /// <summary>All known permissions in the system.</summary>
    public static readonly string[] AllPermissions =
    {
        "gps.own", "gps.read", "gps.write", "gps.delete",
        "dogs.read", "dogs.write", "dogs.owner",
        "categories.read", "categories.write",
        "users.read", "users.write", "users.admin",
        "equipment.read", "equipment.write", "equipment.location",
        "deployments.own", "deployments.manage",
        "config.admin", "maintenance.admin"
    };

    /// <summary>Default role definitions seeded for new tenants.</summary>
    public static readonly Dictionary<string, (string DisplayName, string[] Permissions)> DefaultRoles = new()
    {
        ["helper"] = ("Helfer", new[]
        {
            "gps.own", "gps.write", "deployments.own"
        }),
        ["member"] = ("Mitglied", new[]
        {
            "gps.own", "gps.read", "gps.write", "gps.delete",
            "equipment.read", "equipment.location", "deployments.own"
        }),
        ["teamlead"] = ("Teamleiter", new[]
        {
            "gps.own", "gps.read", "gps.write", "gps.delete",
            "dogs.read", "dogs.write", "dogs.owner",
            "users.read", "users.write",
            "equipment.read", "equipment.write", "equipment.location",
            "deployments.own", "deployments.manage"
        }),
        ["admin"] = ("Administrator", AllPermissions)
    };

    private readonly TenantTableFactory _tables;

    // Cache: tenantId:roleId → permissions array
    private readonly Dictionary<string, string[]> _roleCache = new();
    private readonly object _cacheLock = new();

    public PermissionChecker(TenantTableFactory tables)
    {
        _tables = tables;
    }

    /// <summary>
    /// Get permissions for a user by loading their role from the tenant's tables.
    /// Returns empty array if user or role not found.
    /// </summary>
    public async Task<string[]> GetUserPermissionsAsync(string tenantId, string username)
    {
        var usersTable = _tables.GetTableClient(tenantId, "Users");
        try
        {
            var user = await usersTable.GetEntityAsync<TableEntity>("users", username,
                select: new[] { "RoleId" });
            var roleId = user.Value.GetString("RoleId");
            if (string.IsNullOrEmpty(roleId)) return Array.Empty<string>();
            return await GetRolePermissionsAsync(tenantId, roleId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Get permissions for a role. Uses in-memory cache.
    /// </summary>
    public async Task<string[]> GetRolePermissionsAsync(string tenantId, string roleId)
    {
        var cacheKey = $"{tenantId}:{roleId}";

        lock (_cacheLock)
        {
            if (_roleCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var rolesTable = _tables.GetTableClient(tenantId, "Roles");
        try
        {
            var role = await rolesTable.GetEntityAsync<TableEntity>("roles", roleId,
                select: new[] { "Permissions" });
            var json = role.Value.GetString("Permissions") ?? "[]";
            var permissions = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();

            lock (_cacheLock)
            {
                _roleCache[cacheKey] = permissions;
            }
            return permissions;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Check if a set of permissions includes the required permission.</summary>
    public static bool HasPermission(string[] userPermissions, string required)
    {
        return Array.Exists(userPermissions, p => p == required);
    }

    /// <summary>Invalidate cached permissions for a role (call after role update).</summary>
    public void InvalidateRole(string tenantId, string roleId)
    {
        var cacheKey = $"{tenantId}:{roleId}";
        lock (_cacheLock)
        {
            _roleCache.Remove(cacheKey);
        }
    }

    /// <summary>Invalidate all cached permissions (call after bulk changes).</summary>
    public void InvalidateAll()
    {
        lock (_cacheLock)
        {
            _roleCache.Clear();
        }
    }

    /// <summary>
    /// Seed default roles into a tenant's Roles table if it's empty.
    /// </summary>
    public async Task SeedDefaultRolesAsync(string tenantId)
    {
        var rolesTable = _tables.GetTableClient(tenantId, "Roles");

        // Check if any roles exist
        bool hasRoles = false;
        await foreach (var _ in rolesTable.QueryAsync<TableEntity>(
            filter: "PartitionKey eq 'roles'", maxPerPage: 1, select: new[] { "RowKey" }))
        {
            hasRoles = true;
            break;
        }
        if (hasRoles) return;

        foreach (var (roleId, (displayName, permissions)) in DefaultRoles)
        {
            var entity = new TableEntity("roles", roleId)
            {
                { "DisplayName", displayName },
                { "Permissions", JsonSerializer.Serialize(permissions) },
                { "IsDefault", true },
                { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
            };
            await rolesTable.UpsertEntityAsync(entity);
        }
    }
}
