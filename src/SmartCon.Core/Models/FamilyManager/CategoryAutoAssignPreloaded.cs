namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Immutable per-dialog snapshot of the auto-assignment configuration
/// (#241): the enabled rule groups plus the attribute-id → name
/// dictionary for condition resolution. Produced by
/// <c>ICategoryAutoAssignService.PreloadAsync</c>.
/// </summary>
/// <param name="EnabledGroups">Enabled groups of all categories (empty
/// when no rules are configured — evaluation short-circuits to
/// NoMatch).</param>
/// <param name="AttributeNamesById">Resolves
/// <see cref="AssignmentCondition.AttributeId"/> to the library attribute
/// name.</param>
/// <param name="CategoryPathsById">Category id → display full path — lets
/// the batch dialog show the assigned category and the ambiguous
/// candidates without a second repository pass.</param>
/// <param name="HasRules">Precomputed <see cref="EnabledGroups"/>.Count
/// &gt; 0 — lets callers skip work without touching the list.</param>
public sealed record CategoryAutoAssignPreloaded(
    IReadOnlyList<AssignmentRuleGroup> EnabledGroups,
    IReadOnlyDictionary<string, string> AttributeNamesById,
    IReadOnlyDictionary<string, string> CategoryPathsById,
    bool HasRules)
{
    /// <summary>Shared empty state for the no-rules-configured case.</summary>
    public static CategoryAutoAssignPreloaded Empty { get; } =
        new([], new Dictionary<string, string>(), new Dictionary<string, string>(), false);
}
