using System.IO;
using Microsoft.Data.Sqlite;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class LocalCatalogDatabase
{
    static LocalCatalogDatabase()
    {
        SQLitePCL.Batteries.Init();
    }

    private readonly object _switchLock = new();
    private string _dbPath;
    private string _connectionString;
    private string _databaseRoot;

    public LocalCatalogDatabase()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _databaseRoot = Path.Combine(appData, "SmartCon", "FamilyManager", "default");
        _dbPath = Path.Combine(_databaseRoot, "catalog.db");
        _connectionString = BuildConnectionString(_dbPath);
    }

    public string ConnectionString => _connectionString;

    public string GetDatabaseRoot() => _databaseRoot;

    public SqliteConnection CreateConnection() => new(_connectionString);

    public void SwitchToPath(string databaseRootPath)
    {
        lock (_switchLock)
        {
            _databaseRoot = databaseRootPath;
            _dbPath = Path.Combine(_databaseRoot, "catalog.db");
            _connectionString = BuildConnectionString(_dbPath);
        }
        Directory.CreateDirectory(databaseRootPath);
    }

    public void Checkpoint()
    {
        try
        {
            using var connection = CreateConnection();
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }

    private static string BuildConnectionString(string dbPath)
    {
        return $"Data Source={dbPath};Pooling=false";
    }
}
