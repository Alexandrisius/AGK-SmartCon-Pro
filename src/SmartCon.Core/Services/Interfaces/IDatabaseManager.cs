using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Manages FamilyManager database connections.
/// Each database is a folder containing catalog.db (SQLite) + files/ (managed storage).
/// Registry is persisted locally in %APPDATA%\SmartCon\FamilyManager\registry.json.
/// </summary>
public interface IDatabaseManager
{
    /// <summary>Asynchronously initialize the manager after construction.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>List all registered database connections.</summary>
    IReadOnlyList<DatabaseConnection> ListConnections();

    /// <summary>Get the currently active database connection, or null if none.</summary>
    DatabaseConnection? GetActiveConnection();

    /// <summary>Get the absolute path to the active database root folder, or null if none.</summary>
    string? GetActiveDatabasePath();

    /// <summary>Create a new database at the specified path.</summary>
    Task<DatabaseConnection> CreateDatabaseAsync(string name, string path, CancellationToken ct = default);

    /// <summary>
    /// Create a new project-scoped database (see #119). Like
    /// <see cref="CreateDatabaseAsync"/> but also stores the supplied
    /// <paramref name="binding"/> in <c>registry.json</c> (connection
    /// <c>Kind = Project</c>, <c>ProjectBinding = binding</c>) and updates
    /// <c>catalog.db.database_meta.base_type = 1</c> to mark this database
    /// as project-scoped in the convenience cache.
    /// </summary>
    Task<DatabaseConnection> CreateProjectDatabaseAsync(
        string name,
        string path,
        ProjectBaseBinding binding,
        CancellationToken ct = default);

    /// <summary>
    /// Attach or overwrite the project binding of an existing connection
    /// (see #119). The connection's <c>Kind</c> is set to <c>Project</c> and
    /// its <c>ProjectBinding</c> replaced with <paramref name="binding"/> in
    /// <c>registry.json</c>. If the connection was previously General, the
    /// cached <c>catalog.db.database_meta.base_type</c> is bumped from 0 to 1.
    /// Idempotent: calling this twice on the same connection only changes
    /// the binding.
    /// </summary>
    Task<DatabaseConnection> ConfigureProjectBaseAsync(
        string connectionId,
        ProjectBaseBinding binding,
        CancellationToken ct = default);

    /// <summary>
    /// Convert a project-scoped connection back to a general base (see #168).
    /// The connection's <c>Kind</c> is set to <c>General</c> and its
    /// <c>ProjectBinding</c> is cleared both in <c>registry.json</c> and in
    /// the source-of-truth <c>catalog.db.database_meta</c>
    /// (<c>base_type = 0</c>, <c>project_binding_json = NULL</c>), so the
    /// conversion survives disconnect/reconnect (ADR-045 Update A1).
    /// The base stops auto-activating on document name match.
    /// Idempotent: calling this on an already-General connection without a
    /// binding is a no-op that returns the connection unchanged; a General
    /// connection with a leftover <c>ProjectBinding</c> (inconsistent state)
    /// is normalized — the binding is cleared everywhere.
    /// </summary>
    Task<DatabaseConnection> ConvertToGeneralBaseAsync(
        string connectionId,
        CancellationToken ct = default);

    /// <summary>Register an existing database at the specified path.</summary>
    Task<DatabaseConnection> ConnectDatabaseAsync(string path, CancellationToken ct = default);

    /// <summary>Switch to the specified database connection.</summary>
    Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Unregister a database connection (does NOT delete files on disk).</summary>
    Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Delete a database and all its files on disk. Deleting the active or last database is allowed; the active connection becomes null.</summary>
    Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Event raised when the active database changes. Argument is null when no database remains connected.</summary>
    event EventHandler<string?>? ActiveDatabaseChanged;
}
