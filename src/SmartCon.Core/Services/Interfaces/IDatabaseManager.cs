using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Manages FamilyManager database connections.
/// Each database is a folder containing catalog.db (SQLite) + files/ (managed storage).
/// Registry is persisted locally in %APPDATA%\SmartCon\FamilyManager\registry.json.
/// </summary>
public interface IDatabaseManager
{
    /// <summary>List all registered database connections.</summary>
    IReadOnlyList<DatabaseConnection> ListConnections();

    /// <summary>Get the currently active database connection, or null if none.</summary>
    DatabaseConnection? GetActiveConnection();

    /// <summary>Get the absolute path to the active database root folder, or null if none.</summary>
    string? GetActiveDatabasePath();

    /// <summary>Create a new database at the specified path.</summary>
    Task<DatabaseConnection> CreateDatabaseAsync(string name, string path, CancellationToken ct = default);

    /// <summary>Register an existing database at the specified path.</summary>
    Task<DatabaseConnection> ConnectDatabaseAsync(string path, CancellationToken ct = default);

    /// <summary>Switch to the specified database connection.</summary>
    Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Unregister a database connection (does NOT delete files on disk).</summary>
    Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Delete a database and all its files on disk. Cannot delete the active database.</summary>
    Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Event raised when the active database changes.</summary>
    event EventHandler<string>? ActiveDatabaseChanged;
}
