namespace SmartCon.Core.Models;

public sealed record UpdateInfo(
    string Version,
    string TagName,
    string? ReleaseNotes,
    DateTime PublishedAt,
    string DownloadUrl,
    long FileSize,
    string AssetName
);
