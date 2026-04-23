namespace SmartCon.Core.Models;

public sealed record FileNameTemplate
{
    public string Delimiter { get; init; } = "-";
    public List<FileBlockDefinition> Blocks { get; init; } = [];
    public List<StatusMapping> StatusMappings { get; init; } = [];

    public static FileNameTemplate Empty => new();
}
