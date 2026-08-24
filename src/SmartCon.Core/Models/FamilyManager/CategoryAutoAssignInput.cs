namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Normalized input for the auto-assignment engine (#241): the family's
/// validation input (per-type parameter values, mapped from the import
/// snapshot), the attribute-name dictionary for condition resolution and
/// the built-in system values. Produced by the FamilyManager-side service;
/// the engine itself is pure.
/// </summary>
/// <param name="ValidationInput">Per-type parameter values
/// (<see cref="Services.Implementation.SnapshotValidationMapper"/> output
/// shape).</param>
/// <param name="AttributeNamesById">Resolves
/// <see cref="AssignmentCondition.AttributeId"/> to the library attribute
/// (parameter) name. A condition whose id is missing here never matches
/// (dangling reference defense).</param>
/// <param name="RevitCategoryOrdinal"><c>BuiltInCategory</c> ordinal of
/// the family, or <c>null</c> when unknown.</param>
/// <param name="Facts">Extracted family facts (Part Type), possibly
/// empty.</param>
/// <param name="FamilyName">Family / import item display name.</param>
/// <param name="SystemFamilyKey">Locale-invariant system family identity
/// (ADR-064), or <c>null</c> for loadable families.</param>
public sealed record CategoryAutoAssignInput(
    FamilyValidationInput ValidationInput,
    IReadOnlyDictionary<string, string> AttributeNamesById,
    int? RevitCategoryOrdinal,
    IReadOnlyList<FamilyFact> Facts,
    string FamilyName,
    string? SystemFamilyKey);
