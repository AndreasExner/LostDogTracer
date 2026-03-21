using System.Security.Cryptography;
using System.Text;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LostDogTracer.Api.Security;

/// <summary>
/// User authentication against a Users Azure Table.
/// - Login validates username/password via PBKDF2 hash.
/// - Issues HMAC-signed stateless tokens (configurable lifetime).
/// - Seeds a default admin user on first use if the table is empty.
/// </summary>
public class AdminAuth
{
    private const string TableName = "Users";
    private const string Partition = "users";

    public static readonly string[] ValidRoles = { "User", "PowerUser", "Manager", "Administrator" };

    public static int GetRoleLevel(string? role) => role switch
    {
        "Administrator" => 4,
        "Manager" => 3,
        "PowerUser" => 2,
        "User" => 1,
        _ => 0
    };

    /// <summary>Returns 403 Forbidden result for insufficient role.</summary>
    public static IActionResult Forbidden() =>
        new ObjectResult(new { error = "Keine Berechtigung für diese Aktion." }) { StatusCode = 403 };

    private readonly TableServiceClient _tableService;
    private readonly byte[] _tokenSecret;
    private readonly TimeSpan _tokenLifetime;
    private readonly string _seedUsername;
    private readonly string _seedPassword;
    private bool _seeded;

    public AdminAuth(TableServiceClient tableService, string tokenSecret,
        string seedUsername, string seedPassword, TimeSpan? tokenLifetime = null)
    {
        _tableService = tableService;
        _tokenSecret = Encoding.UTF8.GetBytes(tokenSecret);
        _tokenLifetime = tokenLifetime ?? TimeSpan.FromHours(24);
        _seedUsername = seedUsername;
        _seedPassword = seedPassword;
    }

    /// <summary>Ensure table exists and seed default admin if empty.</summary>
    public async Task EnsureSeededAsync()
    {
        if (_seeded) return;
        var table = _tableService.GetTableClient(TableName);
        await table.CreateIfNotExistsAsync();

        // Check if any admin exists
        bool hasUsers = false;
        await foreach (var _ in table.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{Partition}'",
            maxPerPage: 1, select: new[] { "RowKey" }))
        {
            hasUsers = true;
            break;
        }

        if (!hasUsers)
        {
            var entity = new TableEntity(Partition, _seedUsername.ToLowerInvariant())
            {
                { "DisplayName", _seedUsername },
                { "PasswordHash", PasswordHasher.Hash(_seedPassword) },
                { "Role", "Administrator" },
                { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
            };
            await table.UpsertEntityAsync(entity);
        }
        _seeded = true;
    }

    /// <summary>Look up the role for a given username (or null if not found).</summary>
    public async Task<string?> GetUserRoleAsync(string username)
    {
        await EnsureSeededAsync();
        var table = _tableService.GetTableClient(TableName);
        try
        {
            var entity = await table.GetEntityAsync<TableEntity>(Partition, username.ToLowerInvariant());
            return entity.Value.GetString("Role") ?? "User";
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Validate username + password against Users table, return signed token or null.</summary>
    public async Task<string?> LoginAsync(string username, string password)
    {
        await EnsureSeededAsync();
        var table = _tableService.GetTableClient(TableName);

        try
        {
            var entity = await table.GetEntityAsync<TableEntity>(Partition, username.ToLowerInvariant());
            var storedHash = entity.Value.GetString("PasswordHash");
            if (storedHash is null || !PasswordHasher.Verify(password, storedHash))
                return null;

            // Update only LastLogin (merge mode — don't round-trip PasswordHash)
            var patch = new TableEntity(Partition, username.ToLowerInvariant())
            {
                { "LastLogin", DateTimeOffset.UtcNow.ToString("o") }
            };
            await table.UpdateEntityAsync(patch, entity.Value.ETag, TableUpdateMode.Merge);

            return CreateToken(username.ToLowerInvariant());
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // user not found
        }
    }

    /// <summary>Validate a Bearer token from the Authorization header.</summary>
    public bool ValidateToken(HttpRequest req)
    {
        // Use custom header because SWA managed functions proxy strips Authorization header
        var token = req.Headers["X-Admin-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            // Fallback: also check Authorization header (for local dev / standalone)
            var authHeader = req.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = authHeader["Bearer ".Length..].Trim();
        }
        return !string.IsNullOrEmpty(token) && IsTokenValid(token);
    }

    /// <summary>Validate token AND check minimum role level. Returns role level (0 = invalid).</summary>
    public async Task<int> ValidateTokenWithRole(HttpRequest req, int minLevel = 1)
    {
        if (!ValidateToken(req)) return 0;
        var username = GetUsernameFromToken(req);
        if (username is null) return 0;
        var role = await GetUserRoleAsync(username);
        var level = GetRoleLevel(role);
        return level >= minLevel ? level : 0;
    }

    /// <summary>Returns 401 Unauthorized result.</summary>
    public static IActionResult Unauthorized() =>
        new UnauthorizedObjectResult(new { error = "Nicht autorisiert. Bitte zuerst anmelden." });

    // ── Admin user management ────────────────────────────────────

    public async Task<List<object>> GetUsersAsync()
    {
        await EnsureSeededAsync();
        var table = _tableService.GetTableClient(TableName);
        var users = new List<object>();

        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{Partition}'"))
        {
            users.Add(new
            {
                username = entity.RowKey,
                displayName = entity.GetString("DisplayName") ?? entity.RowKey,
                role = entity.GetString("Role") ?? "User",
                createdAt = entity.GetString("CreatedAt") ?? "",
                lastLogin = entity.GetString("LastLogin") ?? ""
            });
        }
        return users;
    }

    public async Task<bool> CreateUserAsync(string username, string displayName, string password, string role = "User")
    {
        await EnsureSeededAsync();
        var table = _tableService.GetTableClient(TableName);
        var key = username.ToLowerInvariant();

        // Validate role
        if (!ValidRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            role = "User";

        // Check if exists
        try
        {
            await table.GetEntityAsync<TableEntity>(Partition, key);
            return false; // already exists
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404) { /* ok */ }

        var entity = new TableEntity(Partition, key)
        {
            { "DisplayName", displayName.Trim() },
            { "PasswordHash", PasswordHasher.Hash(password) },
            { "Role", role },
            { "CreatedAt", DateTimeOffset.UtcNow.ToString("o") }
        };
        await table.AddEntityAsync(entity);
        return true;
    }

    public async Task<bool> ChangePasswordAsync(string username, string oldPassword, string newPassword)
    {
        var table = _tableService.GetTableClient(TableName);
        var key = username.ToLowerInvariant();

        try
        {
            var entity = await table.GetEntityAsync<TableEntity>(Partition, key);
            var storedHash = entity.Value.GetString("PasswordHash");
            if (storedHash is null || !PasswordHasher.Verify(oldPassword, storedHash))
                return false;

            // Use merge patch to only update PasswordHash
            var patch = new TableEntity(Partition, key)
            {
                { "PasswordHash", PasswordHasher.Hash(newPassword) }
            };
            await table.UpsertEntityAsync(patch, TableUpdateMode.Merge);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task<bool> ResetPasswordAsync(string username, string newPassword)
    {
        var table = _tableService.GetTableClient(TableName);
        var key = username.ToLowerInvariant();

        try
        {
            // Verify user exists
            await table.GetEntityAsync<TableEntity>(Partition, key);

            // Use merge patch to only update PasswordHash
            var patch = new TableEntity(Partition, key)
            {
                { "PasswordHash", PasswordHasher.Hash(newPassword) }
            };
            await table.UpsertEntityAsync(patch, TableUpdateMode.Merge);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task<bool> DeleteUserAsync(string username)
    {
        var table = _tableService.GetTableClient(TableName);
        try
        {
            await table.DeleteEntityAsync(Partition, username.ToLowerInvariant());
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Update role and/or displayName for a user (admin action).</summary>
    public async Task<bool> UpdateUserAsync(string username, string? displayName, string? role)
    {
        var table = _tableService.GetTableClient(TableName);
        var key = username.ToLowerInvariant();

        try
        {
            await table.GetEntityAsync<TableEntity>(Partition, key);

            var patch = new TableEntity(Partition, key);
            if (!string.IsNullOrWhiteSpace(displayName))
                patch["DisplayName"] = displayName.Trim();
            if (!string.IsNullOrWhiteSpace(role))
            {
                if (!ValidRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                    role = "User";
                patch["Role"] = role;
            }
            await table.UpdateEntityAsync(patch, Azure.ETag.All, TableUpdateMode.Merge);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Update own display name (self-service).</summary>
    public async Task<bool> UpdateDisplayNameAsync(string username, string displayName)
    {
        var table = _tableService.GetTableClient(TableName);
        var key = username.ToLowerInvariant();

        try
        {
            await table.GetEntityAsync<TableEntity>(Partition, key);
            var patch = new TableEntity(Partition, key)
            {
                { "DisplayName", displayName.Trim() }
            };
            await table.UpdateEntityAsync(patch, Azure.ETag.All, TableUpdateMode.Merge);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>Return user objects with rowKey (username) and displayName for dropdowns.</summary>
    public async Task<List<object>> GetUserNamesAsync()
    {
        await EnsureSeededAsync();
        var table = _tableService.GetTableClient(TableName);
        var items = new List<(string rowKey, string displayName)>();

        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{Partition}'",
            select: new[] { "RowKey", "DisplayName" }))
        {
            var display = entity.GetString("DisplayName") ?? entity.RowKey;
            if (!string.IsNullOrWhiteSpace(display))
                items.Add((entity.RowKey, display));
        }

        items.Sort((a, b) => StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false).Compare(a.displayName, b.displayName));
        return items.Select(i => (object)new { rowKey = i.rowKey, displayName = i.displayName }).ToList();
    }

    /// <summary>Return a map of username → displayName for FK resolution.</summary>
    public async Task<Dictionary<string, string>> GetUserDisplayNameMapAsync()
    {
        await EnsureSeededAsync();
        var table = _tableService.GetTableClient(TableName);
        var map = new Dictionary<string, string>();

        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{Partition}'",
            select: new[] { "RowKey", "DisplayName" }))
        {
            map[entity.RowKey] = entity.GetString("DisplayName") ?? entity.RowKey;
        }
        return map;
    }

    /// <summary>Return login usernames (RowKey), excluding "admin".</summary>
    public async Task<List<string>> GetUserLoginNamesAsync()
    {
        await EnsureSeededAsync();
        var table = _tableService.GetTableClient(TableName);
        var names = new List<string>();

        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{Partition}'",
            select: new[] { "RowKey" }))
        {
            var username = entity.RowKey;
            if (!string.IsNullOrWhiteSpace(username) &&
                !string.Equals(username, "admin", StringComparison.OrdinalIgnoreCase))
                names.Add(username);
        }

        names.Sort(StringComparer.Create(new System.Globalization.CultureInfo("de-DE"), false));
        return names;
    }

    // ── Token creation & validation ──────────────────────────────

    private string CreateToken(string username)
    {
        var expires = DateTimeOffset.UtcNow.Add(_tokenLifetime).ToUnixTimeSeconds();
        var payload = $"{username}|{expires}";
        var sig = Sign(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
             + "."
             + Convert.ToBase64String(sig);
    }

    /// <summary>Extract username from a valid token (or null).</summary>
    public string? GetUsernameFromToken(HttpRequest req)
    {
        // Use custom header because SWA managed functions proxy strips Authorization header
        var token = req.Headers["X-Admin-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            var authHeader = req.Headers["Authorization"].FirstOrDefault();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = authHeader["Bearer ".Length..].Trim();
        }
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) return null;

            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var sig = Convert.FromBase64String(parts[1]);

            var expectedSig = Sign(payload);
            if (!CryptographicOperations.FixedTimeEquals(sig, expectedSig)) return null;

            var segments = payload.Split('|');
            if (segments.Length != 2) return null;
            if (!long.TryParse(segments[1], out var expires)) return null;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expires) return null;

            return segments[0];
        }
        catch { return null; }
    }

    private bool IsTokenValid(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) return false;

            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var sig = Convert.FromBase64String(parts[1]);

            var expectedSig = Sign(payload);
            if (!CryptographicOperations.FixedTimeEquals(sig, expectedSig)) return false;

            var segments = payload.Split('|');
            if (segments.Length != 2) return false;
            if (!long.TryParse(segments[1], out var expires)) return false;

            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expires;
        }
        catch { return false; }
    }

    private byte[] Sign(string data)
    {
        using var hmac = new HMACSHA256(_tokenSecret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
