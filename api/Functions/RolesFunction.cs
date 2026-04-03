using System.Net;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

/// <summary>
/// RBAC role management: CRUD for tenant-specific roles with permission assignments.
/// All endpoints require users.admin permission.
/// </summary>
public class RolesFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly PermissionChecker _permChecker;
    private readonly ILogger<RolesFunction> _logger;

    public RolesFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, PermissionChecker permChecker, ILogger<RolesFunction> logger)
    {
        _auth = auth;
        _apiKey = apiKey;
        _rateLimiter = rateLimiter;
        _tables = tables;
        _permChecker = permChecker;
        _logger = logger;
    }

    [Function("GetRoles")]
    public async Task<HttpResponseData> GetRoles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/roles")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.admin");
        if (ctx is null) return permError!;

        try
        {
            var rolesTable = _tables.GetTableClient(ctx.TenantId, "Roles");
            var roles = new List<object>();

            await foreach (var entity in rolesTable.QueryAsync<TableEntity>(filter: "PartitionKey eq 'roles'"))
            {
                var permsJson = entity.GetString("Permissions") ?? "[]";
                var perms = JsonSerializer.Deserialize<string[]>(permsJson) ?? Array.Empty<string>();
                roles.Add(new
                {
                    roleId = entity.RowKey,
                    displayName = entity.GetString("DisplayName") ?? entity.RowKey,
                    permissions = perms,
                    isDefault = entity.GetBoolean("IsDefault") ?? false,
                    createdAt = entity.GetString("CreatedAt") ?? ""
                });
            }

            return req.CreateJsonResponse(HttpStatusCode.OK, new
            {
                roles,
                allPermissions = PermissionChecker.AllPermissions
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading roles");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("CreateRole")]
    public async Task<HttpResponseData> CreateRole(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/roles")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.admin");
        if (ctx is null) return permError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<RoleRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.RoleId) || string.IsNullOrWhiteSpace(body.DisplayName))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "roleId und displayName sind erforderlich.");

        if (body.Permissions is null || body.Permissions.Length == 0)
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Mindestens eine Berechtigung erforderlich.");

        // Validate permissions
        var invalid = body.Permissions.Where(p => !PermissionChecker.AllPermissions.Contains(p)).ToArray();
        if (invalid.Length > 0)
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, $"Unbekannte Berechtigungen: {string.Join(", ", invalid)}");

        try
        {
            var rolesTable = _tables.GetTableClient(ctx.TenantId, "Roles");
            var roleId = body.RoleId.Trim().ToLowerInvariant();

            // Check if exists
            try
            {
                await rolesTable.GetEntityAsync<TableEntity>("roles", roleId);
                return req.CreateErrorResponse(HttpStatusCode.Conflict, $"Rolle '{roleId}' existiert bereits.");
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }

            var entity = new TableEntity("roles", roleId)
            {
                { "DisplayName", InputSanitizer.StripHtml(body.DisplayName.Trim()) },
                { "Permissions", JsonSerializer.Serialize(body.Permissions) },
                { "IsDefault", false },
                { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
            };

            await rolesTable.AddEntityAsync(entity);
            _logger.LogInformation("Role created: {RoleId} in {Tenant}", roleId, ctx.TenantId);

            return req.CreateJsonResponse(HttpStatusCode.Created, new
            {
                roleId,
                displayName = body.DisplayName.Trim(),
                permissions = body.Permissions
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating role");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("UpdateRole")]
    public async Task<HttpResponseData> UpdateRole(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/roles/{roleId}")] HttpRequestData req,
        string roleId)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.admin");
        if (ctx is null) return permError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<RoleRequest>();
        if (body is null) return bodyError!;

        try
        {
            var rolesTable = _tables.GetTableClient(ctx.TenantId, "Roles");
            var entity = (await rolesTable.GetEntityAsync<TableEntity>("roles", roleId)).Value;

            if (!string.IsNullOrWhiteSpace(body.DisplayName))
                entity["DisplayName"] = InputSanitizer.StripHtml(body.DisplayName.Trim());

            if (body.Permissions is not null && body.Permissions.Length > 0)
            {
                var invalid = body.Permissions.Where(p => !PermissionChecker.AllPermissions.Contains(p)).ToArray();
                if (invalid.Length > 0)
                    return req.CreateErrorResponse(HttpStatusCode.BadRequest, $"Unbekannte Berechtigungen: {string.Join(", ", invalid)}");
                entity["Permissions"] = JsonSerializer.Serialize(body.Permissions);
            }

            await rolesTable.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
            _permChecker.InvalidateRole(ctx.TenantId, roleId);

            _logger.LogInformation("Role updated: {RoleId} in {Tenant}", roleId, ctx.TenantId);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Rolle aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Rolle nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating role");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("DeleteRole")]
    public async Task<HttpResponseData> DeleteRole(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/roles/{roleId}")] HttpRequestData req,
        string roleId)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.admin");
        if (ctx is null) return permError!;

        try
        {
            var rolesTable = _tables.GetTableClient(ctx.TenantId, "Roles");
            var entity = (await rolesTable.GetEntityAsync<TableEntity>("roles", roleId)).Value;

            if (entity.GetBoolean("IsDefault") == true)
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Standard-Rollen können nicht gelöscht werden.");

            // Check no users are assigned to this role
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            await foreach (var user in usersTable.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'users'", select: new[] { "RowKey", "RoleId" }))
            {
                if (user.GetString("RoleId") == roleId)
                    return req.CreateErrorResponse(HttpStatusCode.BadRequest,
                        $"Rolle wird noch von Benutzer '{user.RowKey}' verwendet.");
            }

            await rolesTable.DeleteEntityAsync("roles", roleId);
            _permChecker.InvalidateRole(ctx.TenantId, roleId);

            _logger.LogInformation("Role deleted: {RoleId} in {Tenant}", roleId, ctx.TenantId);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Rolle gelöscht." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Rolle nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting role");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    private record RoleRequest(string? RoleId, string? DisplayName, string[]? Permissions);
}
