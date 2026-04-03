using System.Net;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using LostDogTracer.Api.Helpers;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Functions;

/// <summary>
/// Authentication endpoints: login, verify, change password, update profile.
/// </summary>
public class AuthFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly RateLimitProvider _rateLimiter;
    private readonly TenantTableFactory _tables;
    private readonly ILogger<AuthFunction> _logger;

    public AuthFunction(AdminAuth auth, ApiKeyValidator apiKey,
        RateLimitProvider rateLimiter, TenantTableFactory tables,
        ILogger<AuthFunction> logger)
    {
        _auth = auth;
        _apiKey = apiKey;
        _rateLimiter = rateLimiter;
        _tables = tables;
        _logger = logger;
    }

    // ── POST /api/auth/login ─────────────────────────────────────

    [Function("AuthLogin")]
    public async Task<HttpResponseData> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;

        var rateError = req.CheckRateLimit(_rateLimiter.Auth);
        if (rateError is not null) return rateError;

        var (body, bodyError) = await req.ReadJsonBodyAsync<LoginRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.TenantId) ||
            string.IsNullOrWhiteSpace(body.Username) ||
            string.IsNullOrWhiteSpace(body.Password))
        {
            return req.CreateErrorResponse(HttpStatusCode.BadRequest,
                "tenantId, username und password sind erforderlich.");
        }

        try
        {
            var result = await _auth.LoginAsync(body.TenantId.Trim(), body.Username.Trim(), body.Password);
            if (result is null)
            {
                _logger.LogWarning("Failed login: {Tenant}/{User}", body.TenantId, body.Username);
                return req.CreateErrorResponse(HttpStatusCode.Unauthorized,
                    "Benutzername oder Passwort falsch.");
            }

            _logger.LogInformation("Login OK: {Tenant}/{User}", body.TenantId, body.Username);
            return req.CreateJsonResponse(HttpStatusCode.OK, new
            {
                token = result.Token,
                permissions = result.Permissions,
                displayName = result.DisplayName,
                roleName = result.RoleName,
                roleId = result.RoleId
            });
        }
        catch (ArgumentException ex)
        {
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login error for {Tenant}/{User}", body.TenantId, body.Username);
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    // ── GET /api/auth/verify ─────────────────────────────────────

    [Function("AuthVerify")]
    public async Task<HttpResponseData> Verify(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/verify")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;

        var rateError = req.CheckRateLimit(_rateLimiter.Read);
        if (rateError is not null) return rateError;

        try
        {
            var info = await _auth.GetFullAuthInfoAsync(req);
            if (info is null)
                return req.CreateErrorResponse(HttpStatusCode.Unauthorized, "Token ungültig oder abgelaufen.");

            var (ctx, permissions, roleId, roleName, displayName) = info.Value;
            return req.CreateJsonResponse(HttpStatusCode.OK, new
            {
                valid = true,
                tenantId = ctx.TenantId,
                username = ctx.Username,
                permissions,
                roleId,
                roleName,
                displayName
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Verify error");
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    // ── POST /api/auth/change-password ───────────────────────────

    [Function("AuthChangePassword")]
    public async Task<HttpResponseData> ChangePassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/change-password")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;

        var rateError = req.CheckRateLimit(_rateLimiter.Auth);
        if (rateError is not null) return rateError;

        var (authCtx, tokenError) = req.ValidateToken(_auth);
        if (authCtx is null) return tokenError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<ChangePasswordRequest>();
        if (body is null) return bodyError!;

        if (string.IsNullOrWhiteSpace(body.OldPassword) || string.IsNullOrWhiteSpace(body.NewPassword))
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Altes und neues Passwort erforderlich.");

        if (body.NewPassword.Length < 8)
            return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Passwort muss mindestens 8 Zeichen lang sein.");

        try
        {
            var usersTable = _tables.GetTableClient(authCtx.TenantId, "Users");
            var entity = await usersTable.GetEntityAsync<TableEntity>("users", authCtx.Username);
            var storedHash = entity.Value.GetString("PasswordHash");

            if (storedHash is null || !PasswordHasher.Verify(body.OldPassword, storedHash))
                return req.CreateErrorResponse(HttpStatusCode.Unauthorized, "Altes Passwort ist falsch.");

            var patch = new TableEntity("users", authCtx.Username)
            {
                { "PasswordHash", PasswordHasher.Hash(body.NewPassword) }
            };
            await usersTable.UpsertEntityAsync(patch, TableUpdateMode.Merge);

            _logger.LogInformation("Password changed: {Tenant}/{User}", authCtx.TenantId, authCtx.Username);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Passwort geändert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Benutzer nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Change password error for {User}", authCtx.Username);
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    // ── POST /api/auth/update-profile ────────────────────────────

    [Function("AuthUpdateProfile")]
    public async Task<HttpResponseData> UpdateProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/update-profile")] HttpRequestData req)
    {
        var keyError = req.ValidateApiKey(_apiKey);
        if (keyError is not null) return keyError;

        var rateError = req.CheckRateLimit(_rateLimiter.Write);
        if (rateError is not null) return rateError;

        var (authCtx, tokenError) = req.ValidateToken(_auth);
        if (authCtx is null) return tokenError!;

        var (body, bodyError) = await req.ReadJsonBodyAsync<UpdateProfileRequest>();
        if (body is null) return bodyError!;

        try
        {
            var usersTable = _tables.GetTableClient(authCtx.TenantId, "Users");
            await usersTable.GetEntityAsync<TableEntity>("users", authCtx.Username);

            var patch = new TableEntity("users", authCtx.Username);
            bool hasUpdate = false;

            if (!string.IsNullOrWhiteSpace(body.DisplayName))
            {
                patch["DisplayName"] = InputSanitizer.StripHtml(body.DisplayName.Trim());
                hasUpdate = true;
            }
            if (body.Location is not null)
            {
                patch["Location"] = InputSanitizer.StripHtml(body.Location.Trim());
                hasUpdate = true;
            }
            if (body.Latitude.HasValue) { patch["Latitude"] = body.Latitude.Value; hasUpdate = true; }
            if (body.Longitude.HasValue) { patch["Longitude"] = body.Longitude.Value; hasUpdate = true; }

            if (!hasUpdate)
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, "Keine Änderungen angegeben.");

            await usersTable.UpdateEntityAsync(patch, Azure.ETag.All, TableUpdateMode.Merge);
            return req.CreateJsonResponse(HttpStatusCode.OK, new { message = "Profil aktualisiert." });
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return req.CreateErrorResponse(HttpStatusCode.NotFound, "Benutzer nicht gefunden.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update profile error for {User}", authCtx.Username);
            return req.CreateErrorResponse(HttpStatusCode.InternalServerError, "Interner Fehler.");
        }
    }

    // ── Request DTOs ─────────────────────────────────────────────

    private record LoginRequest(string? TenantId, string? Username, string? Password);
    private record ChangePasswordRequest(string? OldPassword, string? NewPassword);
    private record UpdateProfileRequest(string? DisplayName, string? Location, double? Latitude, double? Longitude);
}
