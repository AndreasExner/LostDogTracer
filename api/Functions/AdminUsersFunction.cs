using System.Text.Json;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class AdminUsersFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly ILogger<AdminUsersFunction> _logger;
    private readonly RateLimitProvider _rateLimit;

    public AdminUsersFunction(AdminAuth auth, ApiKeyValidator apiKey, ILogger<AdminUsersFunction> logger, RateLimitProvider rateLimit)
    {
        _auth = auth;
        _apiKey = apiKey;
        _logger = logger;
        _rateLimit = rateLimit;
    }

    [Function("GetAdminUsers")]
    public async Task<IActionResult> GetUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/admin-users")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (!_auth.ValidateToken(req))
                return AdminAuth.Unauthorized();

            var users = await _auth.GetUsersAsync();
            return new OkObjectResult(users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading admin users");
            return new StatusCodeResult(500);
        }
    }

    [Function("CreateAdminUser")]
    public async Task<IActionResult> CreateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/admin-users")] HttpRequest req)
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

            var body = await JsonSerializer.DeserializeAsync<CreateUserRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.Username) ||
                string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.Password))
                return new BadRequestObjectResult(new { error = "Benutzername, Anzeigename und Kennwort erforderlich" });

            if (body.Password.Length < 8)
                return new BadRequestObjectResult(new { error = "Kennwort muss mindestens 8 Zeichen haben" });

            var ok = await _auth.CreateUserAsync(body.Username, body.DisplayName, body.Password);
            if (!ok)
                return new ConflictObjectResult(new { error = $"Benutzer '{body.Username}' existiert bereits" });

            _logger.LogInformation("Admin user created: {User}", body.Username);
            return new CreatedResult("", new { username = body.Username.ToLowerInvariant(), displayName = body.DisplayName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating admin user");
            return new StatusCodeResult(500);
        }
    }

    [Function("ResetAdminPassword")]
    public async Task<IActionResult> ResetPassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/admin-users/{username}/reset-password")] HttpRequest req,
        string username)
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

            var body = await JsonSerializer.DeserializeAsync<ResetPasswordRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.NewPassword))
                return new BadRequestObjectResult(new { error = "Neues Kennwort erforderlich" });

            if (body.NewPassword.Length < 8)
                return new BadRequestObjectResult(new { error = "Kennwort muss mindestens 8 Zeichen haben" });

            var ok = await _auth.ResetPasswordAsync(username, body.NewPassword);
            if (!ok)
                return new NotFoundObjectResult(new { error = "Benutzer nicht gefunden" });

            _logger.LogInformation("Password reset for admin user: {User}", username);
            return new OkObjectResult(new { message = $"Kennwort fuer '{username}' zurueckgesetzt" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteAdminUser")]
    public async Task<IActionResult> DeleteUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/admin-users/{username}")] HttpRequest req,
        string username)
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

            // Prevent self-deletion
            var currentUser = _auth.GetUsernameFromToken(req);
            if (string.Equals(currentUser, username, StringComparison.OrdinalIgnoreCase))
                return new BadRequestObjectResult(new { error = "Sie können sich nicht selbst löschen" });

            var ok = await _auth.DeleteUserAsync(username);
            if (!ok)
                return new NotFoundObjectResult(new { error = "Benutzer nicht gefunden" });

            _logger.LogInformation("Admin user deleted: {User}", username);
            return new OkObjectResult(new { message = $"Benutzer '{username}' geloescht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting admin user");
            return new StatusCodeResult(500);
        }
    }

    private record CreateUserRequest
    {
        public string? Username { get; init; }
        public string? DisplayName { get; init; }
        public string? Password { get; init; }
    }

    private record ResetPasswordRequest
    {
        public string? NewPassword { get; init; }
    }
}
