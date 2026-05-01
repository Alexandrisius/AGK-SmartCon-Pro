using System.IO;
using System.Reflection;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.Tests.FamilyManager.Repository;

internal sealed class TempCatalogFixture : IDisposable
{
    public string TempDir { get; }
    public string DbPath { get; }
    public string ConnectionString { get; }

    private readonly LocalCatalogDatabase _database;
    private readonly LocalCatalogMigrator _migrator;
    private readonly LocalCatalogProvider _provider;
    private readonly StoragePathResolver _pathResolver;

    public TempCatalogFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"SmartConTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
        DbPath = Path.Combine(TempDir, "catalog.db");
        ConnectionString = $"Data Source={DbPath}";

        _database = new LocalCatalogDatabase();
        _database.SwitchToPath(TempDir);
        _migrator = new LocalCatalogMigrator(_database);
        _provider = new LocalCatalogProvider(_database);
        _pathResolver = new StoragePathResolver(_database);
    }

    public LocalCatalogDatabase GetDatabase() => _database;
    public LocalCatalogMigrator GetMigrator() => _migrator;
    public LocalCatalogProvider GetProvider() => _provider;
    public StoragePathResolver GetPathResolver() => _pathResolver;
    public string GetDatabaseRoot() => _database.GetDatabaseRoot();

    public async Task MigrateAsync()
    {
        await _migrator.MigrateAsync();
    }

    public string CreateFakeRfaFile(string fileName)
    {
        var path = Path.Combine(TempDir, fileName);
        File.WriteAllText(path, $"FAKE_RFA_CONTENT_{fileName}_{Guid.NewGuid()}");
        return path;
    }

    public string CreateFakeRfaFileWithContent(string fileName, byte[] content)
    {
        var path = Path.Combine(TempDir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, true);
        }
        catch
        {
        }
    }
}
