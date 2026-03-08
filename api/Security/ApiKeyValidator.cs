namespace LostDogTracer.Api.Security;

/// <summary>
/// Validates the X-API-Key header against the configured key.
/// </summary>
public class ApiKeyValidator
{
    private readonly string _apiKey;

    public ApiKeyValidator(string apiKey)
    {
        _apiKey = apiKey;
    }

    /// <summary>Returns true if the X-API-Key header matches.</summary>
    public bool IsValid(Microsoft.AspNetCore.Http.HttpRequest req)
    {
        var key = req.Headers["X-API-Key"].FirstOrDefault();
        return !string.IsNullOrEmpty(key) && key == _apiKey;
    }
}
