using System.Security.Cryptography;
using System.Text;
using Azure.Data.Tables;
using LostDogTracer.Api.Helpers;

namespace LostDogTracer.Api.Security;

/// <summary>
/// Result of a successful token validation.
/// </summary>
public record AuthContext(string TenantId, string Username);

/// <summary>
/// Successful login result.
/// </summary>
public record LoginResult(
    string Token,
    string[] Permissions,
    string DisplayName,
    string RoleName,
    string RoleId);

/// <summary>
/// Multi-tenant user authentication against Azure Table Storage.
/// - Token format: base64({tenantId}|{username}|{expiry}|{hmac-signature})
/// - Seeds default admin user + roles on first use per tenant.
/// - Stateless tokens — no server-side session storage.
/// </summary>
public class AdminAuth
{
    private const string UserPartition = "users";

    private readonly TenantTableFactory _tables;
    private readonly PermissionChecker _permissionChecker;
    private readonly byte[] _tokenSecret;
    private readonly TimeSpan _tokenLifetime;
    private readonly string _seedUsername;
    private readonly string _seedPassword;

    private readonly HashSet<string> _seededTenants = new();
    private readonly object _seedLock = new();

    public AdminAuth(
        TenantTableFactory tables,
        PermissionChecker permissionChecker,
        string tokenSecret,
        string seedUsername,
        string seedPassword,
        TimeSpan? tokenLifetime = null)
    {
        _tables = tables;
        _permissionChecker = permissionChecker;
        _tokenSecret = Encoding.UTF8.GetBytes(tokenSecret);
        _tokenLifetime = tokenLifetime ?? TimeSpan.FromHours(24);
        _seedUsername = seedUsername;
        _seedPassword = seedPassword;
    }

    // ── Seed ─────────────────────────────────────────────────────

    /// <summary>
    /// Ensure tables exist and seed default admin + roles for a tenant.
    /// Idempotent — skips if already seeded this instance lifetime.
    /// </summary>
    public async Task EnsureSeededAsync(string tenantId)
    {
        lock (_seedLock)
        {
            if (_seededTenants.Contains(tenantId)) return;
        }

        await _tables.EnsureTablesExistAsync(tenantId);
        await _permissionChecker.SeedDefaultRolesAsync(tenantId);

        var usersTable = _tables.GetTableClient(tenantId, "Users");
        bool hasUsers = false;
        await foreach (var _ in usersTable.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{UserPartition}'",
            maxPerPage: 1, select: new[] { "RowKey" }))
        {
            hasUsers = true;
            break;
        }

        if (!hasUsers)
        {
            var entity = new TableEntity(UserPartition, _seedUsername.ToLowerInvariant())
            {
                { "DisplayName", _seedUsername },
                { "PasswordHash", PasswordHasher.Hash(_seedPassword) },
                { "RoleId", "admin" },
                { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
            };
            await usersTable.UpsertEntityAsync(entity);
        }

        lock (_seedLock)
        {
            _seededTenants.Add(tenantId);
        }
    }

    // ── Login ────────────────────────────────────────────────────

    /// <summary>
    /// Validate credentials and return token + user info, or null on failure.
    /// </summary>
    public async Task<LoginResult?> LoginAsync(string tenantId, string username, string password)
    {
        await EnsureSeededAsync(tenantId);
        var usersTable = _tables.GetTableClient(tenantId, "Users");

        try
        {
            var entity = await usersTable.GetEntityAsync<TableEntity>(
                UserPartition, username.ToLowerInvariant());
            var storedHash = entity.Value.GetString("PasswordHash");
            if (storedHash is null || !PasswordHasher.Verify(password, storedHash))
                return null;

            // Update LastLogin (merge — don't round-trip hash)
            var patch = new TableEntity(UserPartition, username.ToLowerInvariant())
            {
                { "LastLogin", DateTimeOffset.UtcNow.ToString("o") }
            };
            await usersTable.UpdateEntityAsync(patch, entity.Value.ETag, TableUpdateMode.Merge);

            var roleId = entity.Value.GetString("RoleId") ?? "helper";
            var permissions = await _permissionChecker.GetRolePermissionsAsync(tenantId, roleId);
            var displayName = entity.Value.GetString("DisplayName") ?? username;
            var roleName = await GetRoleDisplayNameAsync(tenantId, roleId);
            var token = CreateToken(tenantId, username.ToLowerInvariant());

            return new LoginResult(token, permissions, displayName, roleName, roleId);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // ── Token validation ─────────────────────────────────────────

    /// <summary>
    /// Extract and validate token from request headers.
    /// Returns AuthContext on success, null on failure.
    /// </summary>
    public AuthContext? ValidateToken(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
    {
        var token = ExtractToken(req);
        if (string.IsNullOrEmpty(token)) return null;
        return ParseAndValidateToken(token);
    }

    /// <summary>
    /// Validate token + check that user has the required permission.
    /// Returns (AuthContext, permissions) or null.
    /// </summary>
    public async Task<(AuthContext Context, string[] Permissions)?> ValidateWithPermissionAsync(
        Microsoft.Azure.Functions.Worker.Http.HttpRequestData req, string requiredPermission)
    {
        var ctx = ValidateToken(req);
        if (ctx is null) return null;

        var permissions = await _permissionChecker.GetUserPermissionsAsync(ctx.TenantId, ctx.Username);
        if (!PermissionChecker.HasPermission(permissions, requiredPermission))
            return null;

        return (ctx, permissions);
    }

    /// <summary>
    /// Validate token and return full auth info (for /auth/verify).
    /// </summary>
    public async Task<(AuthContext Context, string[] Permissions, string RoleId, string RoleName, string DisplayName)?>
        GetFullAuthInfoAsync(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
    {
        var ctx = ValidateToken(req);
        if (ctx is null) return null;

        var usersTable = _tables.GetTableClient(ctx.TenantId, "Users");
        try
        {
            var user = await usersTable.GetEntityAsync<TableEntity>(
                UserPartition, ctx.Username,
                select: new[] { "RoleId", "DisplayName" });

            var roleId = user.Value.GetString("RoleId") ?? "helper";
            var displayName = user.Value.GetString("DisplayName") ?? ctx.Username;
            var permissions = await _permissionChecker.GetRolePermissionsAsync(ctx.TenantId, roleId);
            var roleName = await GetRoleDisplayNameAsync(ctx.TenantId, roleId);

            return (ctx, permissions, roleId, roleName, displayName);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<string> GetRoleDisplayNameAsync(string tenantId, string roleId)
    {
        try
        {
            var rolesTable = _tables.GetTableClient(tenantId, "Roles");
            var role = await rolesTable.GetEntityAsync<TableEntity>("roles", roleId,
                select: new[] { "DisplayName" });
            return role.Value.GetString("DisplayName") ?? roleId;
        }
        catch (Azure.RequestFailedException)
        {
            return roleId;
        }
    }

    // ── Token creation & parsing ─────────────────────────────────

    private string CreateToken(string tenantId, string username)
    {
        var expiry = DateTimeOffset.UtcNow.Add(_tokenLifetime).ToUnixTimeSeconds();
        var payload = $"{tenantId}|{username}|{expiry}";
        var signature = ComputeSignature(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{payload}|{signature}"));
    }

    private AuthContext? ParseAndValidateToken(string token)
    {
        if (token.Length > 1024) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var parts = decoded.Split('|');
            if (parts.Length != 4) return null;

            var tenantId = parts[0];
            var username = parts[1];
            var expiryStr = parts[2];
            var signature = parts[3];

            var payload = $"{tenantId}|{username}|{expiryStr}";
            var expectedSig = ComputeSignature(payload);
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedSig)))
                return null;

            if (!long.TryParse(expiryStr, out var expiry)) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiry) return null;

            return new AuthContext(tenantId, username);
        }
        catch
        {
            return null;
        }
    }

    private string ComputeSignature(string payload)
    {
        using var hmac = new HMACSHA256(_tokenSecret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private static string? ExtractToken(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req)
    {
        if (req.Headers.TryGetValues("X-Admin-Token", out var tokenValues))
        {
            var token = tokenValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(token)) return token;
        }

        if (req.Headers.TryGetValues("Authorization", out var authValues))
        {
            var authHeader = authValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return authHeader["Bearer ".Length..].Trim();
            }
        }

        return null;
    }
}
