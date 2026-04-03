namespace LostDogTracer.Api.Helpers;

/// <summary>
/// Generates tenant-scoped blob paths for photo uploads.
/// Pattern: photos/{tenantId}/{username}/{rowKey}.{ext}
/// </summary>
public static class BlobHelper
{
    public const string PhotoContainer = "photos";

    /// <summary>
    /// Build the blob path for a photo upload.
    /// </summary>
    public static string GetPhotoPath(string tenantId, string username, string rowKey, string extension)
    {
        // Normalize extension (remove leading dot if present)
        extension = extension.TrimStart('.');
        return $"{tenantId}/{username}/{rowKey}.{extension}";
    }

    /// <summary>
    /// Extract the blob name from a full photo URL (e.g. SAS URL).
    /// Returns null if the URL doesn't match the expected pattern.
    /// </summary>
    public static string? ExtractBlobName(string? photoUrl)
    {
        if (string.IsNullOrEmpty(photoUrl)) return null;
        try
        {
            var uri = new Uri(photoUrl);
            // Path is /photos/tenantId/username/rowKey.ext
            var path = uri.AbsolutePath;
            var containerPrefix = $"/{PhotoContainer}/";
            var idx = path.IndexOf(containerPrefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return path[(idx + containerPrefix.Length)..];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Allowed photo extensions.</summary>
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    /// <summary>Maximum photo file size in bytes (5 MB).</summary>
    public const long MaxPhotoSize = 5 * 1024 * 1024;
}
