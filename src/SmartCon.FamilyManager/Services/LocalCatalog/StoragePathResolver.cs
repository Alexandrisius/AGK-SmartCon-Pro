using System.IO;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// Resolves canonical on-disk paths for the catalog's managed storage
/// area. Exposed as <c>public</c> because
/// <c>FamilyManagerServices</c> (also public) and the
/// <c>FamilyManagerMainViewModel</c> constructor need to inject it;
/// keeping it internal would force a wider
/// <c>InternalsVisibleTo</c> chain than is justified by a single
/// "all catalog-managed files live under {dbRoot}/files/..." invariant.
/// </summary>
public sealed class StoragePathResolver
{
    private readonly LocalCatalogDatabase _database;

    public StoragePathResolver(LocalCatalogDatabase database)
    {
        _database = database;
    }

    public string GetDatabaseRoot()
    {
        return _database.GetDatabaseRoot();
    }

    public string GetFilesRoot()
    {
        return Path.Combine(GetDatabaseRoot(), "files");
    }

    public string GetFamilyDirectory(string catalogItemId)
    {
        return Path.Combine(GetFilesRoot(), catalogItemId);
    }

    public string GetVersionDirectory(string catalogItemId, string versionLabel)
    {
        return Path.Combine(GetFamilyDirectory(catalogItemId), versionLabel);
    }

    public string GetRevitFileDirectory(string catalogItemId, string versionLabel, int revitMajorVersion)
    {
        return Path.Combine(GetVersionDirectory(catalogItemId, versionLabel), $"r{revitMajorVersion}");
    }

    public string GetRfaFilePath(string catalogItemId, string versionLabel, int revitMajorVersion, string fileName)
    {
        return Path.Combine(GetRevitFileDirectory(catalogItemId, versionLabel, revitMajorVersion), fileName);
    }

    /// <summary>New flat path without r{revit} subfolder. Used for all new imports.</summary>
    public string GetRfaFilePath(string catalogItemId, string versionLabel, string fileName)
    {
        return Path.Combine(GetVersionDirectory(catalogItemId, versionLabel), fileName);
    }

    public string GetRvtFilePath(string catalogItemId, string versionLabel, string fileName)
    {
        return Path.Combine(GetVersionDirectory(catalogItemId, versionLabel), fileName);
    }

    /// <summary>File name of the derived avatar thumbnail (ADR-047 / issue #131).</summary>
    public const string AvatarFileName = "avatar.png";

    /// <summary>
    /// Path of the derived avatar thumbnail for a catalog item:
    /// {db-root}/files/{family-id}/avatar.png. Version-independent — the avatar
    /// represents the whole family, not a single version (ADR-047).
    /// </summary>
    public string GetAvatarPath(string catalogItemId)
    {
        return Path.Combine(GetFamilyDirectory(catalogItemId), AvatarFileName);
    }

    public string GetAssetsDirectory(string catalogItemId, string versionLabel)
    {
        return Path.Combine(GetVersionDirectory(catalogItemId, versionLabel), "assets");
    }

    public string GetAssetTypeDirectory(string catalogItemId, string versionLabel, string assetTypeFolder)
    {
        return Path.Combine(GetAssetsDirectory(catalogItemId, versionLabel), assetTypeFolder);
    }

    public string GetRelativePath(string absolutePath)
    {
        var root = GetDatabaseRoot();
        if (absolutePath.StartsWith(root))
        {
            var relative = absolutePath[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative;
        }
        return absolutePath;
    }

    public static string GetAssetTypeFolder(FamilyAssetType? assetType) => assetType switch
    {
        FamilyAssetType.Image => "images",
        FamilyAssetType.Video => "videos",
        FamilyAssetType.Document => "documents",
        FamilyAssetType.Model3D => "models",
        FamilyAssetType.LookupTable => "lookup",
        FamilyAssetType.Spreadsheet => "spreadsheets",
        _ => "other"
    };

    /// <summary>Legacy path with r{revit} subfolder. Kept for backward compat when reading old files.</summary>
    public void EnsureFamilyDirectories(string catalogItemId, string versionLabel, int revitMajorVersion)
    {
        var revitDir = GetRevitFileDirectory(catalogItemId, versionLabel, revitMajorVersion);
        Directory.CreateDirectory(revitDir);
    }

    /// <summary>New flat path without r{revit} subfolder. Used for all new imports.</summary>
    public void EnsureFamilyDirectories(string catalogItemId, string versionLabel)
    {
        var dir = GetVersionDirectory(catalogItemId, versionLabel);
        Directory.CreateDirectory(dir);
    }

    public void EnsureAssetDirectory(string catalogItemId, string versionLabel, string assetTypeFolder)
    {
        var dir = GetAssetTypeDirectory(catalogItemId, versionLabel, assetTypeFolder);
        Directory.CreateDirectory(dir);
    }
}
