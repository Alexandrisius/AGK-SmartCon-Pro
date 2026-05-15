using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyTypeRepository
{
    Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default);
    Task SaveTypesAsync(string catalogItemId, IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default);
    Task SaveTypesForRunAsync(string catalogItemId, string? versionId, string? fileId, string runId, IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default);
    Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default);
}
