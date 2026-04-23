namespace SmartCon.Core.Models;

public sealed record PurgeOptions
{
    public bool PurgeRvtLinks { get; init; } = true;
    public bool PurgeCadImports { get; init; } = true;
    public bool PurgeImages { get; init; } = true;
    public bool PurgePointClouds { get; init; } = true;
    public bool PurgeGroups { get; init; } = true;
    public bool PurgeAssemblies { get; init; } = true;
    public bool PurgeSpaces { get; init; } = true;
    public bool PurgeRebar { get; init; } = true;
    public bool PurgeFabricReinforcement { get; init; } = true;
    public bool PurgeSheets { get; init; } = true;
    public bool PurgeSchedules { get; init; } = true;
    public bool PurgeUnused { get; init; } = true;
}
