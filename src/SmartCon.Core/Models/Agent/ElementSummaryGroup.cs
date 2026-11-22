namespace SmartCon.Core.Models.Agent;

public sealed class ElementSummaryGroup
{
    public required string Key { get; init; }

    public int Count { get; init; }

    public double? Sum { get; init; }

    public string? SumUnit { get; init; }

    /// <summary>
    /// Family name of the group (groupBy=type and groupBy=family). Absent when the
    /// elements have no family or when several families share the group key.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>
    /// Element type id of the group (groupBy=type). Absent when several types share
    /// the group key.
    /// </summary>
    public long? TypeId { get; init; }

    /// <summary>
    /// Category display name of the group (groupBy=type and groupBy=family). Absent
    /// when several categories share the group key.
    /// </summary>
    public string? Category { get; init; }
}
