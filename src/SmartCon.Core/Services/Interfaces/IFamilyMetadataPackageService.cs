using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public sealed class FamilyMetadataImportResult
{
    public int CategoriesImported { get; init; }
    public int AttributesImported { get; init; }
    public int BindingsImported { get; init; }
    public int BindingsSkipped { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IFamilyMetadataPackageService
{
    Task<FamilyMetadataPackage> ExportCategoriesAsync(CancellationToken ct = default);
    Task<FamilyMetadataPackage> ExportAttributesAsync(CancellationToken ct = default);
    Task<FamilyMetadataPackage> ExportFullAsync(CancellationToken ct = default);
    Task<FamilyMetadataImportResult> ImportAsync(FamilyMetadataPackage package, CancellationToken ct = default);
}
