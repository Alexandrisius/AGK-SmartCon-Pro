namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// A connection to a FamilyManager database at a specific path.
/// The database folder contains catalog.db (SQLite) + files/ (managed storage).
/// </summary>
/// <param name="Id">Unique identifier for this connection entry.</param>
/// <param name="Name">User-friendly display name.</param>
/// <param name="Path">Absolute path to the database root folder (contains catalog.db).</param>
/// <param name="CreatedAtUtc">When this connection was registered.</param>
public sealed record DatabaseConnection(
    string Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAtUtc);
