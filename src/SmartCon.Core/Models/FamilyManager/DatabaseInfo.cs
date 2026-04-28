namespace SmartCon.Core.Models.FamilyManager;

public sealed record DatabaseInfo(
    string Id,
    string Name,
    DateTimeOffset CreatedAtUtc);
