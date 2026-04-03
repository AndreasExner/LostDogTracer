using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using LostDogTracer.Api.Security;

namespace LostDogTracer.Api.Helpers;

/// <summary>
/// Extension methods on HttpRequestData to eliminate boilerplate in Azure Functions.
/// Provides API key validation, rate limiting, auth+permission checks, JSON I/O.
/// </summary>
public static class FunctionHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // ── API Key ──────────────────────────────────────────────────

    /// <summary>
    /// Validate X-API-Key header. Returns error response if invalid, null if OK.
    /// </summary>
    public static HttpResponseData? ValidateApiKey(this HttpRequestData req, ApiKeyValidator validator)
    {
        if (validator.IsValid(req)) return null;
        return req.CreateErrorResponse(HttpStatusCode.Unauthorized, "Ungültiger API-Key.");
    }

    // ── Rate Limiting ────────────────────────────────────────────

    /// <summary>
    /// Check rate limit for the caller's IP. Returns 429 response if exceeded, null if OK.
    /// </summary>
    public static HttpResponseData? CheckRateLimit(this HttpRequestData req, RateLimiter limiter)
    {
        var ip = GetClientIp(req);
        if (limiter.IsAllowed(ip)) return null;
        return req.CreateErrorResponse(HttpStatusCode.TooManyRequests, "Zu viele Anfragen. Bitte warten.");
    }

    // ── Auth + Permission ────────────────────────────────────────

    /// <summary>
    /// Validate token (no permission check). Returns AuthContext or error response.
    /// </summary>
    public static (AuthContext? Auth, HttpResponseData? Error) ValidateToken(
        this HttpRequestData req, AdminAuth auth)
    {
        var ctx = auth.ValidateToken(req);
        if (ctx is null)
            return (null, req.CreateErrorResponse(HttpStatusCode.Unauthorized,
                "Nicht autorisiert. Bitte zuerst anmelden."));
        return (ctx, null);
    }

    /// <summary>
    /// Validate token + check required permission. Returns (AuthContext, permissions) or error.
    /// </summary>
    public static async Task<(AuthContext? Auth, string[]? Permissions, HttpResponseData? Error)>
        RequirePermissionAsync(this HttpRequestData req, AdminAuth auth, string permission)
    {
        var result = await auth.ValidateWithPermissionAsync(req, permission);
        if (result is null)
        {
            // Distinguish 401 (no/invalid token) from 403 (valid token, no permission)
            var ctx = auth.ValidateToken(req);
            if (ctx is null)
                return (null, null, req.CreateErrorResponse(HttpStatusCode.Unauthorized,
                    "Nicht autorisiert. Bitte zuerst anmelden."));
            return (null, null, req.CreateErrorResponse(HttpStatusCode.Forbidden,
                "Keine Berechtigung für diese Aktion."));
        }

        return (result.Value.Context, result.Value.Permissions, null);
    }

    // ── JSON I/O ─────────────────────────────────────────────────

    /// <summary>
    /// Read and deserialize JSON request body. Returns null + 400 response on failure.
    /// </summary>
    public static async Task<(T? Body, HttpResponseData? Error)> ReadJsonBodyAsync<T>(
        this HttpRequestData req) where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions);
            if (body is null)
                return (null, req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültiger Request-Body."));
            return (body, null);
        }
        catch (JsonException)
        {
            return (null, req.CreateErrorResponse(HttpStatusCode.BadRequest, "Ungültiges JSON-Format."));
        }
    }

    /// <summary>Create a JSON success response.</summary>
    public static HttpResponseData CreateJsonResponse(this HttpRequestData req,
        HttpStatusCode statusCode, object data)
    {
        var response = req.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        var json = JsonSerializer.Serialize(data, JsonOptions);
        response.WriteString(json);
        return response;
    }

    /// <summary>Create a JSON error response: { error: "..." }.</summary>
    public static HttpResponseData CreateErrorResponse(this HttpRequestData req,
        HttpStatusCode statusCode, string message)
    {
        return req.CreateJsonResponse(statusCode, new { error = message });
    }

    // ── Query helpers ────────────────────────────────────────────

    /// <summary>Get a query parameter value or null.</summary>
    public static string? GetQueryParam(this HttpRequestData req, string name)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        return query[name];
    }

    /// <summary>Get a query parameter as int, or default value.</summary>
    public static int GetQueryInt(this HttpRequestData req, string name, int defaultValue)
    {
        var val = req.GetQueryParam(name);
        return int.TryParse(val, out var result) ? result : defaultValue;
    }

    // ── Internal helpers ─────────────────────────────────────────

    private static string GetClientIp(HttpRequestData req)
    {
        // Azure Functions Worker: check forwarded headers first
        if (req.Headers.TryGetValues("X-Forwarded-For", out var forwarded))
        {
            var ip = forwarded.FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(ip)) return ip;
        }
        return "unknown";
    }
}
