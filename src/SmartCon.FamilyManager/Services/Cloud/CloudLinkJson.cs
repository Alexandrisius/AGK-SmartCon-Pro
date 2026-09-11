using System.IO;
using System.Text.Json;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.Cloud;

/// <summary>
/// Canonical JSON of <see cref="CloudLink"/> — shared by the registry DTO
/// duplication path and <c>catalog.db.database_meta.remote_source_json</c>
/// (both roles write the same shape; self-heal reads it back).
/// </summary>
internal static class CloudLinkJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(CloudLink link) => JsonSerializer.Serialize(link, Options);

    public static CloudLink? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CloudLink>(json!, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Well-known local paths of the cloud catalog module (master plan §7.4).</summary>
internal static class CloudPaths
{
    /// <summary>Root of all Subscribed copies: %APPDATA%\SmartCon\FamilyManager\cloud\{slug}\.</summary>
    public static string SubscriptionRoot(string slug) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon", "FamilyManager", "cloud", slug);

    /// <summary>sync-state.json marker of a subscribed copy (written after each successful pull).</summary>
    public static string SyncStatePath(string subscriptionRoot) => Path.Combine(subscriptionRoot, "sync-state.json");

    /// <summary>
    /// Preview cache OUTSIDE the copy (ADR-075 §7): a Subscribed copy is
    /// read-only, so lazily generated GLBs land here — deterministic path by
    /// (item, version, type), computable without extraction.
    /// </summary>
    public static string PreviewCacheFilePath(string catalogItemId, string versionLabel, string typeName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartCon", "FamilyManager", "cloud-cache", "previews");
        return Path.Combine(root,
            $"{SanitizeFileNamePart(catalogItemId)}_{SanitizeFileNamePart(versionLabel)}_{SanitizeFileNamePart(typeName)}.glb");
    }

    /// <summary>net48-friendly invalid-char scrub (type names contain ':', '/', etc.).</summary>
    private static string SanitizeFileNamePart(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }
        return sb.Length > 0 ? sb.ToString() : "_";
    }
}
