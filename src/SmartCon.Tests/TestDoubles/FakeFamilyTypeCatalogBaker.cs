using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Test double for <see cref="IFamilyTypeCatalogBaker"/>.
/// Simulates a successful bake by copying the source .rfa to the output path
/// and reporting the number of catalog entries as baked types.
/// </summary>
internal sealed class FakeFamilyTypeCatalogBaker : IFamilyTypeCatalogBaker
{
    public Task<FamilyTypeCatalogBakingResult> BakeAsync(
        string sourceRfaPath,
        TypeCatalogParseResult catalog,
        string outputRfaPath,
        CancellationToken ct = default)
    {
        var outputDirectory = Path.GetDirectoryName(outputRfaPath);
        if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.Copy(sourceRfaPath, outputRfaPath, overwrite: true);

        return Task.FromResult(new FamilyTypeCatalogBakingResult(
            Success: true,
            OutputRfaPath: outputRfaPath,
            BakedTypeCount: catalog.Entries.Count,
            ErrorMessage: null));
    }

    public Task<FamilyTypeCatalogBakingResult> BakeInExistingDocumentAsync(
        object familyDoc,
        TypeCatalogParseResult catalog,
        CancellationToken ct = default)
    {
        return Task.FromResult(new FamilyTypeCatalogBakingResult(
            Success: true,
            OutputRfaPath: null,
            BakedTypeCount: catalog.Entries.Count,
            ErrorMessage: null));
    }
}
