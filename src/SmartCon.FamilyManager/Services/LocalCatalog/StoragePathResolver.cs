using System.IO;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class StoragePathResolver
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

    public void EnsureFamilyDirectories(string catalogItemId, string versionLabel, int revitMajorVersion)
    {
        var revitDir = GetRevitFileDirectory(catalogItemId, versionLabel, revitMajorVersion);
        Directory.CreateDirectory(revitDir);
    }

    public void EnsureAssetDirectory(string catalogItemId, string versionLabel, string assetTypeFolder)
    {
        var dir = GetAssetTypeDirectory(catalogItemId, versionLabel, assetTypeFolder);
        Directory.CreateDirectory(dir);
    }
}
