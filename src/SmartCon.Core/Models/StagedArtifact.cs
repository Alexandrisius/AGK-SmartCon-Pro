namespace SmartCon.Core.Models;

/// <summary>A single staged artifact targeting a specific Revit version group.</summary>
public sealed record StagedArtifact(
    string StagingPath,
    string TargetInstallPath,
    string ArtifactTag
);
