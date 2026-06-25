using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// Extracts minimal file-level metadata (name + last write time).
/// Sha256/size are no longer computed: v2.0.0 removed them from the catalog
/// and we no longer need to detect duplicates by content hash.
/// </summary>
internal sealed class FileMetadataExtractionService : IFamilyMetadataExtractionService
{
    public Task<FamilyMetadataExtractionResult> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);

        return Task.FromResult(new FamilyMetadataExtractionResult(
            FileName: fileInfo.Name.Trim(),
            LastWriteTimeUtc: fileInfo.LastWriteTimeUtc,
            CategoryName: null,
            RevitMajorVersion: null,
            Types: null,
            Parameters: null));
    }
}
