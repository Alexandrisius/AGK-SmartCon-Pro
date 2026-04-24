namespace SmartCon.Core.Models;

public sealed record ExportMapping
{
    public string Field { get; init; } = string.Empty;
    public string SourceValue { get; init; } = string.Empty;
    public string TargetValue { get; init; } = string.Empty;
}
