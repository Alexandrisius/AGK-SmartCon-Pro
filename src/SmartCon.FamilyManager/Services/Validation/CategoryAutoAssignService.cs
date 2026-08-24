using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Validation;

namespace SmartCon.FamilyManager.Services.Validation;

/// <summary>
/// Local-catalog implementation of the auto-assignment orchestration
/// (#241): preloads the enabled rule groups + attribute names once per
/// dialog, then maps each import snapshot to
/// <see cref="CategoryAutoAssignInput"/> (via
/// <see cref="SnapshotValidationMapper"/> for the per-type parameter
/// values) and runs the pure <see cref="ICategoryAutoAssignEngine"/>.
/// </summary>
internal sealed class CategoryAutoAssignService : ICategoryAutoAssignService
{
    private readonly IAssignmentRuleRepository _ruleRepository;
    private readonly IAttributeDefinitionRepository _attributeRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly ICategoryAutoAssignEngine _engine;

    public CategoryAutoAssignService(
        IAssignmentRuleRepository ruleRepository,
        IAttributeDefinitionRepository attributeRepository,
        ICategoryRepository categoryRepository,
        ICategoryAutoAssignEngine engine)
    {
        _ruleRepository = ruleRepository;
        _attributeRepository = attributeRepository;
        _categoryRepository = categoryRepository;
        _engine = engine;
    }

    public async Task<CategoryAutoAssignPreloaded> PreloadAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("AutoAssign", ("Method", nameof(PreloadAsync)));

        var groups = await _ruleRepository.GetGroupsWithConditionsAsync(ct);
        var enabledGroups = groups.Where(g => g.IsEnabled).ToList();

        // All definitions (not only active): a deactivated attribute still
        // has a name — deactivation must not silently break conditions.
        var attributes = await _attributeRepository.GetAllAsync(ct);
        var namesById = attributes.ToDictionary(a => a.Id, a => a.Name);

        var categories = await _categoryRepository.GetAllAsync(ct);
        var pathsById = categories.ToDictionary(c => c.Id, c => c.FullPath);

        SmartConLogger.Debug($"Rules preloaded: groups={enabledGroups.Count}, attributes={namesById.Count}, categories={pathsById.Count}");

        return new CategoryAutoAssignPreloaded(enabledGroups, namesById, pathsById, enabledGroups.Count > 0);
    }

    public CategoryAutoAssignResult Evaluate(
        CategoryAutoAssignPreloaded preloaded,
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        string? familyNameOverride = null)
    {
        if (!preloaded.HasRules || (loadableSnapshot is null && systemSnapshot is null))
        {
            return CategoryAutoAssignResult.NoMatch;
        }

        var input = BuildInput(preloaded, loadableSnapshot, systemSnapshot, familyNameOverride);
        var result = _engine.Evaluate(input, preloaded.EnabledGroups);

        SmartConLogger.Debug(
            $"Evaluate: family='{input.FamilyName}', outcome={result.Outcome}" +
            (result.CategoryId is not null ? $", category={result.CategoryId}" : string.Empty) +
            (result.CandidateCategoryIds.Count > 0
                ? $", candidates={result.CandidateCategoryIds.Count}"
                : string.Empty));

        return result;
    }

    private static CategoryAutoAssignInput BuildInput(
        CategoryAutoAssignPreloaded preloaded,
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        string? familyNameOverride)
    {
        var validationInput = loadableSnapshot is not null
            ? SnapshotValidationMapper.ToValidationInput(loadableSnapshot)
            : SnapshotValidationMapper.ToValidationInput(systemSnapshot!);

        int? revitCategoryOrdinal;
        IReadOnlyList<FamilyFact> facts;
        string familyName;
        string? systemFamilyKey = null;

        if (loadableSnapshot is not null)
        {
            revitCategoryOrdinal = loadableSnapshot.CategoryId;
            facts = loadableSnapshot.Facts ?? [];
            familyName = familyNameOverride ?? loadableSnapshot.FamilyName;
        }
        else
        {
            var snapshot = systemSnapshot!;
            revitCategoryOrdinal = snapshot.CategoryId;
            facts = [];

            familyName = familyNameOverride
                         ?? snapshot.Types.Select(t => t.FamilyName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                         ?? snapshot.CategoryName;

            systemFamilyKey = snapshot.Types.Select(t => t.FamilyKey).FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
        }

        return new CategoryAutoAssignInput(
            validationInput,
            preloaded.AttributeNamesById,
            revitCategoryOrdinal,
            facts,
            familyName,
            systemFamilyKey,
            BuildDepths(preloaded.CategoryPathsById));
    }

    /// <summary>Catalog tree depth per category id (path segment count) —
    /// feeds the engine's specificity tie-break (#241).</summary>
    private static IReadOnlyDictionary<string, int> BuildDepths(IReadOnlyDictionary<string, string> pathsById)
    {
        var depths = new Dictionary<string, int>(pathsById.Count);
        foreach (var pair in pathsById)
        {
            var count = 0;
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                count = pair.Value.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries).Length;
            }

            depths[pair.Key] = Math.Max(1, count);
        }

        return depths;
    }

    private static readonly string[] PathSeparator = [" > "];
}
