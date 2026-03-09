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
                _logger.LogWarning("Failed admin login attempt for user: {User}", body.Username);
                return new UnauthorizedObjectResult(new { error = "Benutzername oder Kennwort falsch" });
            }

            _logger.LogInformation("Admin login successful: {User}", body.Username);
            return new OkObjectResult(new { token });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during admin login");
            return new StatusCodeResult(500);
        }
    }

    [Function("AdminVerify")]
    public IActionResult Verify(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/verify")] HttpRequest req)
    {
        var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_rateLimit.Read.IsAllowed(ip))
            return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

        if (!_auth.ValidateToken(req))
            return AdminAuth.Unauthorized();

        var username = _auth.GetUsernameFromToken(req);
        return new OkObjectResult(new { valid = true, username });
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

            _logger.LogInformation("Password changed for: {User}", username?.Replace("\n", "").Replace("\r", ""));
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
}
