using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class FileNameOnlyMetadataExtractionService : IFamilyMetadataExtractionService
{
    private readonly Sha256FileHasher _hasher;

    public FileNameOnlyMetadataExtractionService(Sha256FileHasher hasher)
    {
        _hasher = hasher;
    }

    public async Task<FamilyMetadataExtractionResult> ExtractAsync(string filePath, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        var sha256 = await _hasher.ComputeHashAsync(filePath, ct);

        return new FamilyMetadataExtractionResult(
            FileName: fileInfo.Name,
            FileSizeBytes: fileInfo.Length,
            Sha256: sha256,
            LastWriteTimeUtc: fileInfo.LastWriteTimeUtc,
            CategoryName: null,
            RevitMajorVersion: null,
            Types: null,
            Parameters: null);
    }
}
