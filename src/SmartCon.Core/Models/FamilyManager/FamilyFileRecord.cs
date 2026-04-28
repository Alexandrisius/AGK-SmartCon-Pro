namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Physical file record for a family (.rfa).
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="OriginalPath">Original file path where imported from.</param>
/// <param name="CachedPath">Relative path in cache (from canonical root).</param>
/// <param name="FileName">File name with extension.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Sha256">SHA-256 content hash.</param>
/// <param name="LastWriteTimeUtc">Last modification time of the source file.</param>
/// <param name="StorageMode">How the file is stored.</param>
public sealed record FamilyFileRecord(
    string Id,
    string? OriginalPath,
    string? CachedPath,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset? LastWriteTimeUtc,
    FamilyFileStorageMode StorageMode);
