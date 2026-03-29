using System.Text.Json;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class AuthFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly ILogger<AuthFunction> _logger;
    private readonly RateLimitProvider _rateLimit;

    public AuthFunction(AdminAuth auth, ApiKeyValidator apiKey, ILogger<AuthFunction> logger, RateLimitProvider rateLimit)
    {
        _auth = auth;
        _apiKey = apiKey;
        _logger = logger;
        _rateLimit = rateLimit;
    }

    [Function("AdminLogin")]
    public async Task<IActionResult> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequest req)
    {
        try
        {
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Auth.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var body = await JsonSerializer.DeserializeAsync<LoginRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
                return new BadRequestObjectResult(new { error = "Benutzername und Kennwort erforderlich" });

            var token = await _auth.LoginAsync(body.Username, body.Password);
            if (token is null)
            {
                _logger.LogWarning("Failed login attempt for user: {User}", body.Username);
                return new UnauthorizedObjectResult(new { error = "Benutzername oder Kennwort falsch" });
            }

            var role = await _auth.GetUserRoleAsync(body.Username) ?? "User";
            var accountant = await _auth.IsAccountantAsync(body.Username);
            _logger.LogInformation("Login successful: {User}", body.Username);
            return new OkObjectResult(new { token, role, accountant });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login");
            return new StatusCodeResult(500);
        }
    }

    [Function("AdminVerify")]
    public async Task<IActionResult> Verify(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/verify")] HttpRequest req)
    {
        var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_rateLimit.Read.IsAllowed(ip))
            return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

        if (!_auth.ValidateToken(req))
            return AdminAuth.Unauthorized();

        var username = _auth.GetUsernameFromToken(req);
        var role = username != null ? await _auth.GetUserRoleAsync(username) ?? "User" : "User";
        var accountant = username != null && await _auth.IsAccountantAsync(username);
        string? displayName = null;
        if (username != null)
        {
            var map = await _auth.GetUserDisplayNameMapAsync();
            displayName = map.GetValueOrDefault(username, username);
        }
        return new OkObjectResult(new { valid = true, username, role, accountant, displayName = displayName ?? username });
    }

    [Function("ChangePassword")]
    public async Task<IActionResult> ChangePassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/change-password")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (!_auth.ValidateToken(req))
                return AdminAuth.Unauthorized();

            var username = _auth.GetUsernameFromToken(req);
            if (username is null) return AdminAuth.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<ChangePasswordRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.OldPassword) || string.IsNullOrWhiteSpace(body.NewPassword))
                return new BadRequestObjectResult(new { error = "Altes und neues Kennwort erforderlich" });

            if (body.NewPassword.Length < 8)
                return new BadRequestObjectResult(new { error = "Neues Kennwort muss mindestens 8 Zeichen haben" });

            var ok = await _auth.ChangePasswordAsync(username, body.OldPassword, body.NewPassword);
            if (!ok)
                return new BadRequestObjectResult(new { error = "Altes Kennwort ist falsch" });

            _logger.LogWarning("Password changed for: {User}", username?.Replace("\n", "").Replace("\r", ""));
            return new OkObjectResult(new { message = "Kennwort geändert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing password");
            return new StatusCodeResult(500);
        }
    }

    private record LoginRequest
    {
        public string? Username { get; init; }
        public string? Password { get; init; }
    }

    private record ChangePasswordRequest
    {
        public string? OldPassword { get; init; }
        public string? NewPassword { get; init; }
    }

    private record UpdateProfileRequest
    {
        public string? DisplayName { get; init; }
        public string? Location { get; init; }
        public double? Latitude { get; init; }
        public double? Longitude { get; init; }
    }

    [Function("UpdateProfile")]
    public async Task<IActionResult> UpdateProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/update-profile")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (!_auth.ValidateToken(req))
                return AdminAuth.Unauthorized();

            var username = _auth.GetUsernameFromToken(req);
            if (username is null) return AdminAuth.Unauthorized();

            var body = await JsonSerializer.DeserializeAsync<UpdateProfileRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null)
                return new BadRequestObjectResult(new { error = "Keine Daten" });

            bool updated = false;

            if (!string.IsNullOrWhiteSpace(body.DisplayName))
            {
                var ok = await _auth.UpdateDisplayNameAsync(username, body.DisplayName);
                if (!ok) return new NotFoundObjectResult(new { error = "Benutzer nicht gefunden" });
                updated = true;
            }

            if (body.Location is not null)
            {
                var ok = await _auth.UpdateUserAsync(username, null, null,
                    body.Location, body.Latitude, body.Longitude);
                if (!ok) return new NotFoundObjectResult(new { error = "Benutzer nicht gefunden" });
                updated = true;
            }

            if (!updated)
                return new BadRequestObjectResult(new { error = "Keine Änderungen" });

            _logger.LogInformation("Profile updated for: {User}", username?.Replace("\n", "").Replace("\r", ""));
            return new OkObjectResult(new { message = "Profil aktualisiert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating profile");
            return new StatusCodeResult(500);
        }
    }
}
