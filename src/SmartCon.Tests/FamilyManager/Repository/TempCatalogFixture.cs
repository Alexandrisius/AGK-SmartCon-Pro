using System.IO;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.TestDoubles;

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
    private readonly LocalFamilyTypeRepository _typeRepository;
    private readonly LocalAttributeValueRepository _valueRepository;
    private readonly LocalFamilyDataImportRunRepository _runRepository;
    private readonly IFamilyTypeCatalogBaker _typeCatalogBaker;

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
        _typeRepository = new LocalFamilyTypeRepository(_database);
        _valueRepository = new LocalAttributeValueRepository(_database);
        _runRepository = new LocalFamilyDataImportRunRepository(_database);
        _typeCatalogBaker = new FakeFamilyTypeCatalogBaker();

        _migrator.MigrateAsync().GetAwaiter().GetResult();
    }

    public LocalCatalogDatabase GetDatabase() => _database;
    public LocalCatalogMigrator GetMigrator() => _migrator;
    public LocalCatalogProvider GetProvider() => _provider;
    public StoragePathResolver GetPathResolver() => _pathResolver;
    public string GetDatabaseRoot() => _database.GetDatabaseRoot();
    public LocalFamilyTypeRepository GetTypeRepository() => _typeRepository;
    public LocalAttributeValueRepository GetValueRepository() => _valueRepository;
    public LocalFamilyDataImportRunRepository GetRunRepository() => _runRepository;
    public IFamilyTypeCatalogBaker GetTypeCatalogBaker() => _typeCatalogBaker;

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

    /// <summary>
    /// Create a fake staged managed file at <c>{dbRoot}/files/&lt;catalogItemId&gt;/&lt;versionLabel&gt;/&lt;fileName&gt;</c>.
    /// Mirrors what <c>StageLoadableFamilyFromProject</c>,
    /// <c>CreateCleanProjectWithTypesAndInstances</c> and
    /// <c>ProcessFamilyImportAsync</c>'s <c>SaveAs</c> do at import time
    /// before the orchestrator's <c>ImportBatchAsync</c> loop calls
    /// <c>ImportFileAsync</c>. The returned path lives under
    /// <c>{dbRoot}/files/</c>, so <c>TryExtractManagedPathInfo</c> should
    /// classify it as a managed file.
    /// </summary>
    public string CreateFakeStagedManagedFile(string catalogItemId, string versionLabel, string fileName)
    {
        var versionDir = Path.Combine(TempDir, "files", catalogItemId, versionLabel);
        Directory.CreateDirectory(versionDir);
        var path = Path.Combine(versionDir, fileName);
        File.WriteAllText(path, $"FAKE_STAGED_MANAGED_CONTENT_{fileName}_{Guid.NewGuid()}");
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
