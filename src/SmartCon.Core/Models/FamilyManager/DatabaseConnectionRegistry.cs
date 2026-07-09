namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Registry of all FamilyManager database connections on this machine.
/// Persisted as registry.json in %APPDATA%\SmartCon\FamilyManager\.
/// </summary>
/// <param name="ActiveConnectionId">ID of the currently active connection.</param>
/// <param name="Connections">All registered connections.</param>
/// <param name="SchemaVersion">Registry file schema version. <c>0</c> for legacy entries written before #119; <c>1</c> after the <c>IRegistryMigrator</c> pass adds <c>kind</c> + <c>projectBinding</c>. Used by <see cref="SmartCon.Core.Services.Interfaces.IRegistryMigrator"/> to decide whether to upgrade the file on load.</param>
public sealed record DatabaseConnectionRegistry(
    string? ActiveConnectionId,
    IReadOnlyList<DatabaseConnection> Connections,
    int SchemaVersion = 0);
