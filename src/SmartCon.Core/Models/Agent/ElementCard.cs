namespace SmartCon.Core.Models.Agent;

public sealed class ElementCard
{
    public required string UniqueId { get; init; }

    public long ElementId { get; init; }

    public long? TypeId { get; init; }

    public long? FamilyId { get; init; }

    public string? FamilyUniqueId { get; init; }

    public string? Category { get; init; }

    public string? Family { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public long? LevelId { get; init; }

    public string? LevelName { get; init; }

    public string? Room { get; init; }

    /// <summary>ALL_MODEL_MARK value; projectable as fields=["mark"] regardless of the UI language (#451).</summary>
    public string? Mark { get; init; }

    /// <summary>ALL_MODEL_INSTANCE_COMMENTS value; projectable as fields=["comments"].</summary>
    public string? Comments { get; init; }

    /// <summary>Name of the phase the element was created in (PHASE_CREATED), for phase-aware diagnosis.</summary>
    public string? PhaseCreated { get; init; }

    public IReadOnlyDictionary<string, string?>? Parameters { get; init; }
}

public sealed class ParameterValueInfo
{
    public required string Name { get; init; }

    public string? Value { get; init; }

    public string? DisplayValue { get; init; }

    /// <summary>Unit label of a measurable parameter (for example "мм").</summary>
    public string? Unit { get; init; }

    public string? StorageType { get; init; }

    /// <summary>system | shared | family</summary>
    public string? ParameterClass { get; init; }

    public bool IsReadOnly { get; init; }

    public string? ReadOnlyReason { get; init; }

    public bool IsShared { get; init; }

    public string? ParameterGuid { get; init; }

    public string? Scope { get; init; }
}

public sealed class ElementDetails
{
    public required string UniqueId { get; init; }

    public long ElementId { get; init; }

    /// <summary>instance | type | element</summary>
    public string Kind { get; init; } = "element";

    public long? TypeId { get; init; }

    public long? FamilyId { get; init; }

    public string? FamilyUniqueId { get; init; }

    /// <summary>loadable | in-place | system</summary>
    public string? FamilyKind { get; init; }

    public string? Category { get; init; }

    public string? Family { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public long? LevelId { get; init; }

    public string? LevelName { get; init; }

    public string? Room { get; init; }

    public IReadOnlyList<ParameterValueInfo> Essence { get; init; } =
        Array.Empty<ParameterValueInfo>();

    public IReadOnlyList<ParameterValueInfo> InstanceParameters { get; init; } =
        Array.Empty<ParameterValueInfo>();

    public int InstanceParametersTotal { get; init; }

    public int InstanceParametersReturned { get; init; }

    public bool InstanceParametersTruncated { get; init; }

    public int FamilyParametersOmitted { get; init; }
}

public sealed class ElementTypeDetails
{
    public long TypeId { get; init; }

    public string? UniqueId { get; init; }

    public string? Name { get; init; }

    public string? FamilyName { get; init; }

    public long? FamilyId { get; init; }

    public string? FamilyUniqueId { get; init; }

    /// <summary>loadable | in-place | system</summary>
    public string? FamilyKind { get; init; }

    public string? Category { get; init; }

    public IReadOnlyList<ParameterValueInfo> Essence { get; init; } =
        Array.Empty<ParameterValueInfo>();

    public IReadOnlyList<ParameterValueInfo>? Parameters { get; init; }

    public int? ParametersTotal { get; init; }

    public int? ParametersReturned { get; init; }

    public bool? ParametersTruncated { get; init; }

    public int FamilyParametersOmitted { get; init; }
}