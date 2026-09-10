namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// A connection to a FamilyManager database at a specific path.
/// The database folder contains catalog.db (SQLite) + files/ (managed storage).
/// </summary>
/// <param name="Id">Unique identifier for this connection entry.</param>
/// <param name="Name">User-friendly display name.</param>
/// <param name="Path">Absolute path to the database root folder (contains catalog.db).</param>
/// <param name="CreatedAtUtc">When this connection was registered.</param>
/// <param name="CurrentUserRole">Optional RBAC role resolved for the active user on this DB (in-memory only, persisted in <c>db_users</c>).</param>
/// <param name="OwnerIdentity">Optional identity of the DB owner. Persisted in <c>database_meta.owner_identity</c>; cached on the connection for quick RBAC checks.</param>
/// <param name="Kind">Whether this is a generic library (<see cref="BaseType.General"/>) or a project-scoped one (<see cref="BaseType.Project"/>) — see #119. Defaults to <see cref="BaseType.General"/> for backward compatibility with legacy <c>registry.json</c> entries.</param>
/// <param name="ProjectBinding">Required only when <see cref="Kind"/> == <see cref="BaseType.Project"/>. Holds the file-name template + field library used to match the active Revit document against this base.</param>
public sealed record DatabaseConnection(
    string Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAtUtc,
    DbUserRole? CurrentUserRole = null,
    string? OwnerIdentity = null,
    BaseType Kind = BaseType.General,
    ProjectBaseBinding? ProjectBinding = null);
