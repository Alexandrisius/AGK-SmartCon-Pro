namespace SmartCon.Core.Models;

public sealed record ExportNameOverride
{
    public Dictionary<string, string> FieldValues { get; init; } = [];
}
