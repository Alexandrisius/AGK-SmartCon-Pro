using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IDatabaseManager
{
    /// <summary>List all available databases.</summary>
    IReadOnlyList<DatabaseInfo> ListDatabases();

    /// <summary>Get the currently active database ID.</summary>
    string GetActiveDatabaseId();

    /// <summary>Create a new database and return its info.</summary>
    Task<DatabaseInfo> CreateDatabaseAsync(string name, CancellationToken ct = default);

    /// <summary>Switch to the specified database. Returns true if successful.</summary>
    Task<bool> SwitchDatabaseAsync(string databaseId, CancellationToken ct = default);

    /// <summary>Delete a database and all its data. Cannot delete the active database.</summary>
    Task<bool> DeleteDatabaseAsync(string databaseId, CancellationToken ct = default);

    /// <summary>Event raised when the active database changes.</summary>
    event EventHandler<string>? ActiveDatabaseChanged;
}
