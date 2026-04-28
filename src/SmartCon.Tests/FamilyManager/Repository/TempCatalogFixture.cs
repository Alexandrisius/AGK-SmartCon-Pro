using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
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

    public TempCatalogFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"SmartConTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
        DbPath = Path.Combine(TempDir, "catalog.db");
        ConnectionString = $"Data Source={DbPath}";

        _database = CreateDatabase(DbPath);
        _migrator = new LocalCatalogMigrator(_database);
        _provider = new LocalCatalogProvider(_database);
    }

    public LocalCatalogDatabase GetDatabase() => _database;
    public LocalCatalogMigrator GetMigrator() => _migrator;
    public LocalCatalogProvider GetProvider() => _provider;

    private static LocalCatalogDatabase CreateDatabase(string path)
    {
        var db = new LocalCatalogDatabase();
        var field = typeof(LocalCatalogDatabase).GetField("_dbPath",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(db, path);

        var csField = typeof(LocalCatalogDatabase).GetField("_connectionString",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        csField.SetValue(db, $"Data Source={path}");

        return db;
    }

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
