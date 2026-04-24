namespace SmartCon.Core.Models;

public sealed record ShareProjectSettings
{
    public string ShareFolderPath { get; init; } = string.Empty;
    public FileNameTemplate FileNameTemplate { get; init; } = new();
    public List<FieldDefinition> FieldLibrary { get; init; } = [];
    public PurgeOptions PurgeOptions { get; init; } = new();
    public List<string> KeepViewNames { get; init; } = [];
    public bool SyncBeforeShare { get; init; } = true;

    public static ShareProjectSettings Empty => new();
}
