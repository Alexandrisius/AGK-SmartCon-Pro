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
}
