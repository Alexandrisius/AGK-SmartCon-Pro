namespace SmartCon.Core.Models.FamilyManager;

public sealed record DatabaseRegistry(
    string ActiveDatabaseId,
    IReadOnlyList<DatabaseInfo> Databases);
