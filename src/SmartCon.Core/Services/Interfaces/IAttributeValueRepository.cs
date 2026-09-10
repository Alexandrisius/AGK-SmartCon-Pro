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

    /// <summary>
    /// Distinct non-empty found values of one attribute across the whole
    /// catalog (#87 advanced search value suggestions). Matches the same
    /// attribute resolution as the filter: attribute_id first, parameter_name
    /// fallback for rows without an attribute binding.
    /// </summary>
    Task<IReadOnlyList<string>> GetDistinctValueTextsAsync(string attributeId, string attributeName, int limit = 100, CancellationToken ct = default);
}
