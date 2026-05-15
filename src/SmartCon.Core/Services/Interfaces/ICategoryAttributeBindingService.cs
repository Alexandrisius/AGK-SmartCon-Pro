using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ICategoryAttributeBindingService
{
    Task<IReadOnlyList<CategoryAttributeBinding>> GetBindingsForCategoryAsync(string categoryId, CancellationToken ct = default);
    Task<IReadOnlyList<EffectiveCategoryAttribute>> GetEffectiveAttributesAsync(string? categoryId, CancellationToken ct = default);
    Task<CategoryAttributeBinding> CreateBindingAsync(string categoryId, string attributeId, int sortOrder, CancellationToken ct = default);
    Task<bool> DeleteBindingAsync(string bindingId, CancellationToken ct = default);
    Task<CategoryAttributeBinding> UpdateBindingAsync(string bindingId, int? sortOrder, bool? isEnabled, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryAttributeBinding>> GetDirectBindingsAsync(string categoryId, CancellationToken ct = default);

    Task<IReadOnlyList<CategoryAttributeBinding>> GetBindingsForAttributeAsync(string attributeId, CancellationToken ct = default);
    Task DeleteBindingsForAttributeAsync(string attributeId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> GetBindingCountsAsync(IEnumerable<string> attributeIds, CancellationToken ct = default);
}
