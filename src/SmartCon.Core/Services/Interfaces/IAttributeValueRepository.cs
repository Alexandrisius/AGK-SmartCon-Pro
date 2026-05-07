using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IAttributeValueRepository
{
    Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForItemAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
    Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForTypeAsync(string typeId, CancellationToken ct = default);
    Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForRunAsync(string runId, CancellationToken ct = default);
    Task SaveValuesAsync(IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default);
    Task ReplaceSnapshotAsync(string catalogItemId, string? versionId, string runId, IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default);
    Task<int> DeleteValuesForRunAsync(string runId, CancellationToken ct = default);
    Task<int> GetFoundCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
    Task<int> GetMissingCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
}
