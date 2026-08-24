namespace SmartCon.Core.Models.FamilyManager;

/// <summary>Outcome of the auto-assignment evaluation (#241).</summary>
public enum CategoryAutoAssignOutcome
{
    /// <summary>No category matched.</summary>
    NoMatch = 0,

    /// <summary>Exactly one category matched.</summary>
    Matched = 1,

    /// <summary>Two or more categories matched — the user must pick one
    /// (the row stays «Без категории» with a warning).</summary>
    Ambiguous = 2,
}

/// <summary>
/// Tri-state result of the auto-assignment evaluation (#241):
/// <see cref="CategoryAutoAssignOutcome.NoMatch"/>,
/// <see cref="CategoryAutoAssignOutcome.Matched"/> (with the winning
/// category id) or <see cref="CategoryAutoAssignOutcome.Ambiguous"/> (with
/// all matching category ids, ordered by group priority).
/// </summary>
public sealed record CategoryAutoAssignResult(
    CategoryAutoAssignOutcome Outcome,
    string? CategoryId,
    IReadOnlyList<string> CandidateCategoryIds)
{
    /// <summary>Singleton for the no-match outcome.</summary>
    public static CategoryAutoAssignResult NoMatch { get; } =
        new(CategoryAutoAssignOutcome.NoMatch, null, Array.Empty<string>());

    /// <summary>Creates the single-match result.</summary>
    public static CategoryAutoAssignResult Matched(string categoryId) =>
        new(CategoryAutoAssignOutcome.Matched, categoryId, Array.Empty<string>());

    /// <summary>Creates the ambiguous result (2+ matching categories).</summary>
    public static CategoryAutoAssignResult Ambiguous(IReadOnlyList<string> categoryIds) =>
        new(CategoryAutoAssignOutcome.Ambiguous, null, categoryIds);
}
