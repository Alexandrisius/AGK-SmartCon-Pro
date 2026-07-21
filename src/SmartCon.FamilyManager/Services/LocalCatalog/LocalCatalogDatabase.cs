using System.IO;
using Microsoft.Data.Sqlite;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

public sealed class LocalCatalogDatabase
{
    static LocalCatalogDatabase()
    {
        SQLitePCL.Batteries.Init();
    }

    private readonly object _switchLock = new();
    private string _dbPath;
    private string _connectionString;
    private string _databaseRoot;
    private bool _canWrite = true;

    public LocalCatalogDatabase()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _databaseRoot = Path.Combine(appData, "SmartCon", "FamilyManager", "default");
        _dbPath = Path.Combine(_databaseRoot, "catalog.db");
        _connectionString = BuildConnectionString(_dbPath, canWrite: true);
        Directory.CreateDirectory(_databaseRoot);
        EnsureJournalModeDeleteOnCreation();
    }

    public string ConnectionString => _connectionString;

    public string GetDatabaseRoot() => _databaseRoot;

    public SqliteConnection CreateConnection() => new(_connectionString);

    public SqliteConnection CreateWritableConnection() => new(BuildConnectionString(_dbPath, canWrite: true));

    public SqliteConnection CreateConnectionForPath(string dbFilePath) => new(BuildConnectionString(dbFilePath, canWrite: true));

    public void SetWriteAccess(bool canWrite)
    {
        lock (_switchLock)
        {
            if (_canWrite == canWrite)
                return;
            _canWrite = canWrite;
            _connectionString = BuildConnectionString(_dbPath, canWrite);
        }
    }

    public void SwitchToPath(string databaseRootPath)
    {
        lock (_switchLock)
        {
            _databaseRoot = databaseRootPath;
            _dbPath = Path.Combine(_databaseRoot, "catalog.db");
            _canWrite = true;
            _connectionString = BuildConnectionString(_dbPath, canWrite: true);

            // Must stay inside the lock: EnsureJournalModeDeleteOnCreation
            // reads _dbPath/_connectionString — outside the lock a concurrent
            // SwitchToPath could repoint them at the other database.
            Directory.CreateDirectory(databaseRootPath);
            EnsureJournalModeDeleteOnCreation();
        }
    }

    private void EnsureJournalModeDeleteOnCreation()
    {
        try
        {
            if (!File.Exists(_dbPath))
                return;
            using var connection = CreateWritableConnection();
            connection.Open();
            EnsureJournalModeDelete(connection);
        }
        catch
        {
            // ignored — will be retried on next connection open
        }
    }

    public void EnsureJournalModeDelete(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var currentMode = cmd.ExecuteScalar() as string;
        if (currentMode is not "delete")
        {
            using var setCmd = connection.CreateCommand();
            setCmd.CommandText = "PRAGMA journal_mode=DELETE;";
            setCmd.ExecuteNonQuery();
        }

        using var busyCmd = connection.CreateCommand();
        busyCmd.CommandText = "PRAGMA busy_timeout=5000;";
        busyCmd.ExecuteNonQuery();
    }

    public async Task EnsureJournalModeDeleteAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var currentMode = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (currentMode is not "delete")
        {
            using var setCmd = connection.CreateCommand();
            setCmd.CommandText = "PRAGMA journal_mode=DELETE;";
            await setCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using var busyCmd = connection.CreateCommand();
        busyCmd.CommandText = "PRAGMA busy_timeout=5000;";
        await busyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string BuildConnectionString(string dbPath, bool canWrite)
    {
        var mode = canWrite ? string.Empty : ";Mode=ReadOnly";
        return $"Data Source={dbPath};Pooling=false;Foreign Keys=True{mode}";
    }
}
