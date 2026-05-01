namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Registry of all FamilyManager database connections on this machine.
/// Persisted as registry.json in %APPDATA%\SmartCon\FamilyManager\.
/// </summary>
/// <param name="ActiveConnectionId">ID of the currently active connection.</param>
/// <param name="Connections">All registered connections.</param>
public sealed record DatabaseConnectionRegistry(
    string? ActiveConnectionId,
    IReadOnlyList<DatabaseConnection> Connections);
