using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IValidationRuleRepository
{
    Task<IReadOnlyList<ValidationRule>> GetRulesForBindingAsync(string bindingId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationRule>> GetRulesForBindingsAsync(IEnumerable<string> bindingIds, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetRuleCountsForBindingsAsync(IEnumerable<string> bindingIds, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetRuleCountsForAttributesAsync(IEnumerable<string> attributeIds, CancellationToken ct = default);
    Task<ValidationRule> CreateRuleAsync(ValidationRule rule, CancellationToken ct = default);
    Task<ValidationRule> UpdateRuleAsync(ValidationRule rule, CancellationToken ct = default);
    Task<bool> DeleteRuleAsync(string ruleId, CancellationToken ct = default);
    Task DeleteRulesForBindingAsync(string bindingId, CancellationToken ct = default);
}
