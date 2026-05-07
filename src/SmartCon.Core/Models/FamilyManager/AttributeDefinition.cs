namespace SmartCon.Core.Models.FamilyManager;

public sealed record AttributeDefinition(
    string Id,
    string Name,
    string? Group,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);
