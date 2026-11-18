namespace SmartCon.Core.Models.Agent;

public sealed class ElementCard
{
    public required string UniqueId { get; init; }

    public long ElementId { get; init; }

    public string? Category { get; init; }

    public string? Family { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public long? LevelId { get; init; }

    public string? LevelName { get; init; }

    public string? Room { get; init; }

    public IReadOnlyDictionary<string, string?>? Parameters { get; init; }
}

public sealed class ParameterValueInfo
{
    public required string Name { get; init; }

    public string? Value { get; init; }

    public string? DisplayValue { get; init; }

    public string? StorageType { get; init; }

    public bool IsReadOnly { get; init; }

    public bool IsShared { get; init; }

    public string? ParameterGuid { get; init; }

    public string? Scope { get; init; }
}

public sealed class ElementDetails
{
    public required string UniqueId { get; init; }

    public long ElementId { get; init; }

    public string? Category { get; init; }

    public string? Family { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? LevelName { get; init; }

    public string? Room { get; init; }

    public bool ParametersTruncated { get; init; }

    public IReadOnlyList<ParameterValueInfo> Parameters { get; init; } = Array.Empty<ParameterValueInfo>();
}