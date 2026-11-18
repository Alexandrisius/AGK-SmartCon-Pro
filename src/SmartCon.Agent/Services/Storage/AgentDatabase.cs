using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Agent.Storage;

namespace SmartCon.Agent.Services.Storage;

public sealed class AgentDatabase
{
    private readonly IReadOnlyList<AgentSchemaMigration> _migrations;

    static AgentDatabase()
    {
        SQLitePCL.Batteries.Init();
    }

    public AgentDatabase()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartCon", "Agent", "agent.db"))
    {
    }

    public AgentDatabase(string databasePath)
        : this(databasePath, AgentSchema.All)
    {
    }

    public AgentDatabase(string databasePath, IReadOnlyList<AgentSchemaMigration> migrations)
    {
        DatabasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
    }

    private const int SchemaVersion = AgentSchema.CurrentVersion;

    public string DatabasePath { get; }

    public object WriteLock { get; } = new();

    public void Initialize()
    {
        lock (WriteLock)
        {
            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var connection = CreateConnection();
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=DELETE;");
            Execute(connection, "PRAGMA busy_timeout=5000;");
            Execute(connection, "PRAGMA foreign_keys=ON;");

            var version = Convert.ToInt32(
                ExecuteScalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

            if (version > SchemaVersion)
            {
                SmartConLogger.Warn(
                    "Agent database schema version " + version + " is newer than supported " +
                    SchemaVersion + "; leaving it untouched [Action: upgrade SmartCon]");
                return;
            }

            if (version == SchemaVersion)
            {
                SelfHeal(connection, version);
                SmartConLogger.Info(
                    "Agent database already up to date (v" + version + "): " +
                    Path.GetFileName(DatabasePath));
                return;
            }

            if (version > 0)
                CreateBackup(connection, version);

            var initialVersion = version;
            foreach (var migration in _migrations.OrderBy(m => m.Version))
            {
                if (migration.Version <= version)
                    continue;

                ApplyMigration(connection, migration);
                version = migration.Version;
            }

            if (initialVersion == 0)
            {
                SmartConLogger.Info(
                    "Agent database initialized (v" + version + "): " +
                    Path.GetFileName(DatabasePath));
            }
            else
            {
                SmartConLogger.Info(
                    "Agent database migrated from v" + initialVersion + " to v" + version + ": " +
                    Path.GetFileName(DatabasePath));
            }
        }
    }

    public SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            ForeignKeys = true
        };
        return new SqliteConnection(builder.ToString());
    }

    private void CreateBackup(SqliteConnection source, int version)
    {
        var backupPath = DatabasePath
            + ".bak-v" + version
            + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        using (var destination = new SqliteConnection(builder.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }

        SmartConLogger.Info("Agent database backup created: " + Path.GetFileName(backupPath));
    }

    private static void ApplyMigration(SqliteConnection connection, AgentSchemaMigration migration)
    {
        // An immediate (write) transaction takes the write lock up front. A deferred
        // transaction would read user_version without a lock and could race another
        // SmartCon process that migrates the same database (MEDIUM-4).
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var currentVersion = Convert.ToInt32(
                ExecuteScalar(connection, "PRAGMA user_version;", transaction),
                CultureInfo.InvariantCulture);

            if (currentVersion >= migration.Version)
            {
                // Another process applied this migration while we were waiting for
                // the write lock: re-checking inside the transaction makes it a no-op.
                transaction.Commit();
                SmartConLogger.Debug(
                    "Agent database migration v" + migration.Version +
                    " skipped: user_version is already " + currentVersion);
                return;
            }

            ApplyStatements(connection, transaction, migration);
            Execute(connection, "PRAGMA user_version=" + migration.Version + ";", transaction);
            transaction.Commit();
        }
        catch
        {
            TryRollback(transaction, migration.Version);
            throw;
        }
    }

    /// <summary>
    /// Repairs a database whose <c>user_version</c> already equals the current
    /// schema version but whose tables/columns are incomplete (a database created
    /// by an intermediate build). Applies only the current migration's statements;
    /// no backup and no user_version change (MEDIUM-3).
    /// </summary>
    private void SelfHeal(SqliteConnection connection, int version)
    {
        AgentSchemaMigration? migration = null;
        foreach (var candidate in _migrations)
        {
            if (candidate.Version == version)
            {
                migration = candidate;
                break;
            }
        }

        if (migration is null)
            return;

        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            ApplyStatements(connection, transaction, migration);
            transaction.Commit();
            SmartConLogger.Debug(
                "Agent database schema self-heal completed for v" + version + ": " +
                Path.GetFileName(DatabasePath));
        }
        catch
        {
            TryRollback(transaction, version);
            throw;
        }
    }

    private static void ApplyStatements(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSchemaMigration migration)
    {
        foreach (var statement in migration.Statements)
        {
            if (statement.Table is not null
                && statement.Column is not null
                && ColumnExists(connection, transaction, statement.Table, statement.Column))
            {
                continue;
            }

            Execute(connection, statement.Sql, transaction);
        }
    }

    private static void TryRollback(SqliteTransaction transaction, int version)
    {
        try
        {
            transaction.Rollback();
        }
        catch (Exception rollbackError)
        {
            SmartConLogger.Warn(
                "Agent database rollback failed for migration v" + version + ": " +
                rollbackError.GetType().Name +
                " [Action: delete the partially migrated agent.db and restart SmartCon]");
        }
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(\"" + table + "\");";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
            command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction is not null)
            command.Transaction = transaction;
        return command.ExecuteScalar();
    }
}
