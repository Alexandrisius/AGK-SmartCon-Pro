namespace SmartCon.Core.Models;

public sealed record PendingUpdate(
    string Version,
    string StagingPath,
    DateTime StagedAt,
    string TargetInstallPath
);
