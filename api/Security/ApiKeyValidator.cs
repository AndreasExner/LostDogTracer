using System.Security.Cryptography;
using System.Text;

namespace LostDogTracer.Api.Security;

/// <summary>
/// Validates the X-API-Key header against the configured key.
/// </summary>
public class ApiKeyValidator
{
    private readonly byte[] _apiKeyBytes;

    public ApiKeyValidator(string apiKey)
    {
        _apiKeyBytes = Encoding.UTF8.GetBytes(apiKey);
    }

    /// <summary>Returns true if the X-API-Key header matches (timing-safe).</summary>
    public bool IsValid(Microsoft.AspNetCore.Http.HttpRequest req)
    {
        var key = req.Headers["X-API-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return false;
        var keyBytes = Encoding.UTF8.GetBytes(key);
        return CryptographicOperations.FixedTimeEquals(keyBytes, _apiKeyBytes);
    }
}
