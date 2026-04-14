namespace SmartCon.Core.Models;

/// <summary>Metadata for a GitHub Release that is newer than the current plugin version.</summary>
public sealed record UpdateInfo(
    string Version,
    string TagName,
    string? ReleaseNotes,
    DateTime PublishedAt,
    string DownloadUrl,
    long FileSize,
    string AssetName
);
