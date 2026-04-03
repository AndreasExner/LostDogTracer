using System.Net;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

public class UsersFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<UsersFunction> _logger;

    public UsersFunction(AdminAuth auth, ApiKeyValidator apiKey, RateLimitProvider rateLimiter,
        TenantTableFactory tables, ILogger<UsersFunction> logger)
    {
        _auth = auth;
        _apiKey = apiKey;
        _rateLimiter = rateLimiter;
        _tables = tables;
        _logger = logger;
    }

    [Function("GetUsers")]
    public async Task<HttpResponseData> GetUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/users")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.read");
        if (ctx is null) return permError!;

        try
        {
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            var users = new List<object>();

            await foreach (var entity in usersTable.QueryAsync<TableEntity>(filter: "PartitionKey eq 'users'"))
            {
                users.Add(new
                {
                    username = entity.RowKey,
                    displayName = entity.GetString("DisplayName") ?? entity.RowKey,
                    roleId = entity.GetString("RoleId") ?? "helper",
                    location = entity.GetString("Location") ?? "",
                    latitude = entity.GetDouble("Latitude"),
                    longitude = entity.GetDouble("Longitude"),
                    createdAt = entity.GetString("CreatedAt") ?? "",
                    lastLogin = entity.GetString("LastLogin") ?? ""
                });
            }
            return req.CreateJsonResponse(HttpStatusCode.OK, users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading users");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("CreateUser")]
    public async Task<HttpResponseData> CreateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/users")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.write");
        if (ctx is null) return permError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<CreateUserRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.DisplayName) ||
            string.IsNullOrWhiteSpace(body.Password))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "username, displayName und password sind erforderlich.");
        if (body.Password.Length < 8)
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Passwort muss mindestens 8 Zeichen haben.");

        try
        {
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            var key = body.Username.Trim().ToLowerInvariant();

            try { await usersTable.GetEntityAsync<TableEntity>("users", key); return req.CreateErrorResponse(HttpStatusCode.Conflict, $"Benutzer '{key}' existiert bereits."); }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { }

            var roleId = body.RoleId ?? "helper";
            var entity = new TableEntity("users", key)
            {
                { "DisplayName", InputSanitizer.StripHtml(body.DisplayName.Trim()) },
                { "PasswordHash", PasswordHasher.Hash(body.Password) },
                { "RoleId", roleId },
                { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
            };
            if (!string.IsNullOrWhiteSpace(body.Location))
                entity["Location"] = InputSanitizer.StripHtml(body.Location.Trim());
            if (body.Latitude.HasValue) entity["Latitude"] = body.Latitude.Value;
            if (body.Longitude.HasValue) entity["Longitude"] = body.Longitude.Value;

            await usersTable.AddEntityAsync(entity);
            return req.CreateJsonResponse(HttpStatusCode.Created, new { username = key, displayName = body.DisplayName.Trim(), roleId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("UpdateUser")]
    public async Task<HttpResponseData> UpdateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/users/{username}")] HttpRequestData req, string username)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.admin");
        if (ctx is null) return permError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<UpdateUserRequest>();
        if (body is null) return bodyError!;

        try
        {
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            var key = username.ToLowerInvariant();
            await usersTable.GetEntityAsync<TableEntity>("users", key);

            var patch = new TableEntity("users", key);
            bool hasUpdate = false;
            if (!string.IsNullOrWhiteSpace(body.DisplayName)) { patch["DisplayName"] = InputSanitizer.StripHtml(body.DisplayName.Trim()); hasUpdate = true; }
            if (!string.IsNullOrWhiteSpace(body.RoleId)) { patch["RoleId"] = body.RoleId.Trim(); hasUpdate = true; }
            if (body.Location is not null) { patch["Location"] = InputSanitizer.StripHtml(body.Location.Trim()); hasUpdate = true; }
            if (body.Latitude.HasValue) { patch["Latitude"] = body.Latitude.Value; hasUpdate = true; }
            if (body.Longitude.HasValue) { patch["Longitude"] = body.Longitude.Value; hasUpdate = true; }

            if (!hasUpdate)
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Keine Änderungen angegeben.");

            await usersTable.UpdateEntityAsync(patch, Azure.ETag.All, TableUpdateMode.Merge);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = $"Benutzer '{username}' aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Benutzer nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("ResetPassword")]
    public async Task<HttpResponseData> ResetPassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/users/{username}/reset-password")] HttpRequestData req, string username)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.admin");
        if (ctx is null) return permError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<ResetPasswordRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.NewPassword) || body.NewPassword.Length < 8)
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Passwort muss mindestens 8 Zeichen haben.");

        try
        {
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            await usersTable.GetEntityAsync<TableEntity>("users", username.ToLowerInvariant());
            var patch = new TableEntity("users", username.ToLowerInvariant())
            {
                { "PasswordHash", PasswordHasher.Hash(body.NewPassword) }
            };
            await usersTable.UpsertEntityAsync(patch, TableUpdateMode.Merge);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = $"Passwort für '{username}' zurückgesetzt." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Benutzer nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("DeleteUser")]
    public async Task<HttpResponseData> DeleteUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/users/{username}")] HttpRequestData req, string username)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;
        var (ctx, _, permError) = await req.RequirePermissionAsync(_auth, "users.admin");
        if (ctx is null) return permError!;

        if (string.Equals(ctx.Username, username, StringComparison.OrdinalIgnoreCase))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Sie können sich nicht selbst löschen.");

        try
        {
            var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
            await usersTable.DeleteEntityAsync("users", username.ToLowerInvariant());
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = $"Benutzer '{username}' gelöscht." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Benutzer nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    [Function("GetUserNames")]
    public async Task<HttpResponseData> GetUserNames(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "user-names")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;
        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;

        // Public endpoint — need tenantId from query param for unauthenticated callers
        var tenantId = req.GetQueryParam("tenantId");
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            var (ctx, _) = req.ValidateToken(_auth);
            if (ctx is not null) tenantId = ctx.TenantId;
        }
        if (string.IsNullOrWhiteSpace(tenantId))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "tenantId ist erforderlich.");

        try
        {
            var usersTable = _tables.GetTableClient(tenantId, "Users");
            var items = new List<(string rowKey, string displayName)>();

            await foreach (var entity in usersTable.QueryAsync<TableEntity>(
                filter: "PartitionKey eq 'users'", select: new[] { "RowKey", "DisplayName" }))
            {
                var display = entity.GetString("DisplayName") ?? entity.RowKey;
                if (!string.IsNullOrWhiteSpace(display))
                    items.Add((entity.RowKey, display));
            }

            var comparer = StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false);
            items.Sort((a, b) => comparer.Compare(a.displayName, b.displayName));
            return req.CreateJsonResponse(HttpStatusCode.OK, items.Select(i => new { rowKey = i.rowKey, displayName = i.displayName }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading user names");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    private record CreateUserRequest(string? Username, string? DisplayName, string? Password, string? RoleId, string? Location, double? Latitude, double? Longitude);
    private record UpdateUserRequest(string? DisplayName, string? RoleId, string? Location, double? Latitude, double? Longitude);
    private record ResetPasswordRequest(string? NewPassword);
}
