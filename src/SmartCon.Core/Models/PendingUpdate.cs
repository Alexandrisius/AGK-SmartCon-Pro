namespace SmartCon.Core.Models;

/// <summary>Describes a staged update awaiting installation on next Revit close.</summary>
public sealed record PendingUpdate(
    string Version,
    string StagingPath,
    DateTime StagedAt,
    string TargetInstallPath
);
