using System.Text.Json;
using LostDogTracer.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace LostDogTracer.Api.Functions;

public class UsersFunction
{
    private readonly AdminAuth _auth;
    private readonly ApiKeyValidator _apiKey;
    private readonly ILogger<UsersFunction> _logger;
    private readonly RateLimitProvider _rateLimit;

    public UsersFunction(AdminAuth auth, ApiKeyValidator apiKey, ILogger<UsersFunction> logger, RateLimitProvider rateLimit)
    {
        _auth = auth;
        _apiKey = apiKey;
        _logger = logger;
        _rateLimit = rateLimit;
    }

    [Function("GetUsers")]
    public async Task<IActionResult> GetUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/users")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _auth.ValidateTokenWithRole(req, 3) == 0)
                return AdminAuth.Forbidden();

            var users = await _auth.GetUsersAsync();
            return new OkObjectResult(users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading users");
            return new StatusCodeResult(500);
        }
    }

    [Function("CreateUser")]
    public async Task<IActionResult> CreateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/users")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            var callerLevel = await _auth.ValidateTokenWithRole(req, 3);
            if (callerLevel == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<CreateUserRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.Username) ||
                string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.Password))
                return new BadRequestObjectResult(new { error = "Benutzername, Anzeigename und Kennwort erforderlich" });

            if (body.Password.Length < 8)
                return new BadRequestObjectResult(new { error = "Kennwort muss mindestens 8 Zeichen haben" });

            // Manager can only assign role "User"
            var role = callerLevel >= 4 ? (body.Role ?? "User") : "User";
            var ok = await _auth.CreateUserAsync(body.Username, body.DisplayName, body.Password, role);
            if (!ok)
                return new ConflictObjectResult(new { error = $"Benutzer '{body.Username}' existiert bereits" });

            _logger.LogInformation("User created: {User}", body.Username);
            return new CreatedResult("", new { username = body.Username.ToLowerInvariant(), displayName = body.DisplayName, role });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating user");
            return new StatusCodeResult(500);
        }
    }

    [Function("ResetPassword")]
    public async Task<IActionResult> ResetPassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/users/{username}/reset-password")] HttpRequest req,
        string username)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _auth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<ResetPasswordRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || string.IsNullOrWhiteSpace(body.NewPassword))
                return new BadRequestObjectResult(new { error = "Neues Kennwort erforderlich" });

            if (body.NewPassword.Length < 8)
                return new BadRequestObjectResult(new { error = "Kennwort muss mindestens 8 Zeichen haben" });

            var ok = await _auth.ResetPasswordAsync(username, body.NewPassword);
            if (!ok)
                return new NotFoundObjectResult(new { error = "Benutzer nicht gefunden" });

            _logger.LogInformation("Password reset for user: {User}", username);
            return new OkObjectResult(new { message = $"Kennwort fuer '{username}' zurueckgesetzt" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting password");
            return new StatusCodeResult(500);
        }
    }

    [Function("DeleteUser")]
    public async Task<IActionResult> DeleteUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/users/{username}")] HttpRequest req,
        string username)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _auth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            // Prevent self-deletion
            var currentUser = _auth.GetUsernameFromToken(req);
            if (string.Equals(currentUser, username, StringComparison.OrdinalIgnoreCase))
                return new BadRequestObjectResult(new { error = "Sie können sich nicht selbst löschen" });

            var ok = await _auth.DeleteUserAsync(username);
            if (!ok)
                return new NotFoundObjectResult(new { error = "Benutzer nicht gefunden" });

            _logger.LogInformation("User deleted: {User}", username);
            return new OkObjectResult(new { message = $"Benutzer '{username}' geloescht" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting user");
            return new StatusCodeResult(500);
        }
    }

    private record CreateUserRequest
    {
        public string? Username { get; init; }
        public string? DisplayName { get; init; }
        public string? Password { get; init; }
        public string? Role { get; init; }
    }

    private record ResetPasswordRequest
    {
        public string? NewPassword { get; init; }
    }

    private record UpdateUserRequest
    {
        public string? DisplayName { get; init; }
        public string? Role { get; init; }
    }

    [Function("UpdateUser")]
    public async Task<IActionResult> UpdateUser(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/users/{username}")] HttpRequest req,
        string username)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Write.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };
            if (await _auth.ValidateTokenWithRole(req, 4) == 0)
                return AdminAuth.Forbidden();

            var body = await JsonSerializer.DeserializeAsync<UpdateUserRequest>(req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (body is null || (string.IsNullOrWhiteSpace(body.DisplayName) && string.IsNullOrWhiteSpace(body.Role)))
                return new BadRequestObjectResult(new { error = "Anzeigename oder Rolle erforderlich" });

            var ok = await _auth.UpdateUserAsync(username, body.DisplayName, body.Role);
            if (!ok)
                return new NotFoundObjectResult(new { error = "Benutzer nicht gefunden" });

            _logger.LogInformation("User updated: {User}", username);
            return new OkObjectResult(new { message = $"Benutzer '{username}' aktualisiert" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user");
            return new StatusCodeResult(500);
        }
    }

    [Function("GetUserNames")]
    public async Task<IActionResult> GetUserNames(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "user-names")] HttpRequest req)
    {
        try
        {
            if (!_apiKey.IsValid(req))
                return new ObjectResult(new { error = "Ungültiger API-Key" }) { StatusCode = 403 };
            var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimit.Read.IsAllowed(ip))
                return new ObjectResult(new { error = "Zu viele Anfragen. Bitte warten." }) { StatusCode = 429 };

            var type = req.Query["type"].FirstOrDefault();
            if (string.Equals(type, "username", StringComparison.OrdinalIgnoreCase))
            {
                // Return usernames (RowKey), excluding "admin"
                var usernames = await _auth.GetUserLoginNamesAsync();
                return new OkObjectResult(usernames);
            }

            var names = await _auth.GetUserNamesAsync();
            return new OkObjectResult(names);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading user names");
            return new StatusCodeResult(500);
        }
    }
}
