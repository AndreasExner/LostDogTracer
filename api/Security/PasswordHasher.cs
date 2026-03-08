using System.Security.Cryptography;

namespace LostDogTracer.Api.Security;

/// <summary>
/// PBKDF2-based password hashing with embedded salt.
/// Format: base64( salt[16] + hash[32] ) — single string, easy to store.
/// Uses 600 000 iterations of HMAC-SHA256 (OWASP 2024 recommendation).
/// </summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;       // 128-bit salt
    private const int HashSize = 32;       // 256-bit hash
    private const int Iterations = 600_000; // OWASP recommended minimum

    /// <summary>Create a hash string from a plaintext password.</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

        // Combine salt + hash into one array → single Base64 string
        var combined = new byte[SaltSize + HashSize];
        salt.CopyTo(combined, 0);
        hash.CopyTo(combined, SaltSize);

        return Convert.ToBase64String(combined);
    }

    /// <summary>Verify a plaintext password against a stored hash.</summary>
    public static bool Verify(string password, string storedHash)
    {
        try
        {
            var combined = Convert.FromBase64String(storedHash);
            if (combined.Length != SaltSize + HashSize) return false;

            var salt = combined[..SaltSize];
            var expectedHash = combined[SaltSize..];

            var actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }
}
