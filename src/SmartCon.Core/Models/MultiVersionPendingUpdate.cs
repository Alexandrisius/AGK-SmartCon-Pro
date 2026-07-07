namespace SmartCon.Core.Models;

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
