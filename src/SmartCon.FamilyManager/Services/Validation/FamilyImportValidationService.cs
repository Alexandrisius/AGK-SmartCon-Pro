using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Validation;

namespace SmartCon.FamilyManager.Services.Validation;

/// <summary>
/// Local-catalog implementation of the import validation gate. Rule
/// resolution: effective attributes of the category (direct + inherited,
/// via <see cref="ICategoryAttributeBindingService"/>) → validation rules
/// attached to those bindings (<see cref="IValidationRuleRepository"/>).
/// Evaluation: pure <see cref="IFamilyValidationEngine"/> over normalized
/// input mapped from the Prepare snapshot or from persisted extraction.
/// </summary>
internal sealed class FamilyImportValidationService : IFamilyImportValidationService
{
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IValidationRuleRepository _ruleRepository;
    private readonly IFamilyValidationEngine _engine;
    private readonly IAttributeValueRepository _valueRepository;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IFamilyDataImportRunRepository _runRepository;

    public FamilyImportValidationService(
        ICategoryAttributeBindingService bindingService,
        IValidationRuleRepository ruleRepository,
        IFamilyValidationEngine engine,
        IAttributeValueRepository valueRepository,
        IFamilyTypeRepository typeRepository,
        IFamilyDataImportRunRepository runRepository)
    {
        _bindingService = bindingService;
        _ruleRepository = ruleRepository;
        _engine = engine;
        _valueRepository = valueRepository;
        _typeRepository = typeRepository;
        _runRepository = runRepository;
    }

    public async Task<IReadOnlyList<EffectiveValidationRule>> GetEffectiveRulesAsync(string? categoryId, CancellationToken ct = default)
    {
        if (categoryId is null)
        {
            return Array.Empty<EffectiveValidationRule>();
        }

        var effectiveAttributes = await _bindingService.GetEffectiveAttributesAsync(categoryId, ct);
        var enabledAttributes = effectiveAttributes
            .Where(a => a.IsEnabled && a.BindingId is not null)
            .ToList();
        if (enabledAttributes.Count == 0)
        {
            return Array.Empty<EffectiveValidationRule>();
        }

        var bindingIds = enabledAttributes.Select(a => a.BindingId!).ToList();
        var rules = await _ruleRepository.GetRulesForBindingsAsync(bindingIds, ct);
        if (rules.Count == 0)
        {
            return Array.Empty<EffectiveValidationRule>();
        }

        var rulesByBinding = rules
            .Where(r => r.IsEnabled)
            .GroupBy(r => r.BindingId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ValidationRule>)g.ToList());

        var result = new List<EffectiveValidationRule>();
        foreach (var attribute in enabledAttributes)
        {
            if (rulesByBinding.TryGetValue(attribute.BindingId!, out var attributeRules))
            {
                foreach (var rule in attributeRules)
                {
                    result.Add(new EffectiveValidationRule(attribute.Name, attribute.IsInherited, rule));
                }
            }
        }

        return result;
    }

    public FamilyValidationReport ValidateFromSnapshots(
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        IReadOnlyList<EffectiveValidationRule> rules)
    {
        if (rules.Count == 0)
        {
            return new FamilyValidationReport(true, Array.Empty<RuleViolation>(), 0);
        }

        FamilyValidationInput? input = loadableSnapshot is not null
            ? SnapshotValidationMapper.ToValidationInput(loadableSnapshot)
            : systemSnapshot is not null
                ? SnapshotValidationMapper.ToValidationInput(systemSnapshot)
                : null;

        if (input is null)
        {
            return new FamilyValidationReport(true, Array.Empty<RuleViolation>(), 0);
        }

        return _engine.Validate(input, rules);
    }

    public async Task<FamilyValidationReport?> ValidateCatalogItemAsync(
        string catalogItemId,
        IReadOnlyList<EffectiveValidationRule> rules,
        CancellationToken ct = default)
    {
        if (rules.Count == 0)
        {
            return new FamilyValidationReport(true, Array.Empty<RuleViolation>(), 0);
        }

        var run = await _runRepository.GetLatestRunForActiveVersionAsync(catalogItemId, ct);
        if (run is null)
        {
            return null;
        }

        var values = await _valueRepository.GetValuesForItemAsync(catalogItemId, run.VersionId, ct);
        if (values.Count == 0)
        {
            return null;
        }

        var types = await _typeRepository.GetTypesForItemVersionAsync(catalogItemId, run.VersionId, ct);
        var typeNames = types.ToDictionary(t => t.Id, t => t.Name);

        var input = ExtractedValuesValidationMapper.ToValidationInput(values, typeNames);
        return _engine.Validate(input, rules);
    }
}
