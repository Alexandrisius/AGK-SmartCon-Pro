namespace SmartCon.Core.Models;

public sealed record FileNameTemplate
{
    public List<FileBlockDefinition> Blocks { get; init; } = [];
    public List<ExportMapping> ExportMappings { get; init; } = [];

    public static FileNameTemplate Empty => new();
}
