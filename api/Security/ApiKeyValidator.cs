using System.Security.Cryptography;
using System.Text;
using Microsoft.Azure.Functions.Worker.Http;

namespace LostDogTracer.Api.Security;

/// <summary>
/// Validates the X-API-Key header against the configured key.
/// Uses timing-safe comparison to prevent timing attacks.
/// </summary>
public class ApiKeyValidator
{
    private readonly byte[] _apiKeyBytes;

    public ApiKeyValidator(string apiKey)
    {
        _apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);
    }

    /// <summary>Returns true if the X-API-Key header matches (timing-safe).</summary>
    public bool IsValid(HttpRequestData req)
    {
        if (!req.Headers.TryGetValues("X-API-Key", out var values)) return false;
        var key = values.FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return false;
        var keyBytes = Encoding.UTF8.GetBytes(key);
        return CryptographicOperations.FixedTimeEquals(keyBytes, _apiKeyBytes);
    }
}
