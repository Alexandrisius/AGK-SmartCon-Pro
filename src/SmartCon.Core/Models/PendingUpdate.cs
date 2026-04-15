namespace SmartCon.Core.Models;

/// <summary>Describes a staged update awaiting installation on next Revit close.</summary>
public sealed record PendingUpdate(
    string Version,
    string StagingPath,
    DateTime StagedAt,
    string TargetInstallPath
);

/// <summary>
/// Multi-version pending update. Contains separate staging directories
/// for each Revit version group, allowing the Updater to update all
/// installed Revit versions at once.
/// </summary>
public sealed record MultiVersionPendingUpdate(
    string Version,
    DateTime StagedAt,
    List<StagedArtifact> Artifacts
);

/// <summary>A single staged artifact targeting a specific Revit version group.</summary>
public sealed record StagedArtifact(
    string StagingPath,
    string TargetInstallPath,
    string ArtifactTag
);
