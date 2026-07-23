namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// A single machine-readable "fact" about a catalog item extracted from the
/// family file at import/actualization time (ADR-055). Facts are driven by
/// <see cref="FamilyFactRuleSet"/>: the family's Revit category determines
/// which facts are extracted (e.g. Part Type for fitting categories).
/// Facts are item-level metadata — they are NOT part of the content hash
/// (<see cref="SmartCon.Core.Services.Implementation.FamilyContentHasher"/>
/// ignores them) and never affect deduplication.
/// </summary>
/// <param name="FactKey">Stable machine key of the fact
/// (e.g. <see cref="FamilyFactRuleSet.PartTypeFactKey"/>).</param>
/// <param name="ValueKey">Stable machine value — for enum-backed facts the
/// enum ordinal as an invariant-culture string (e.g. <c>"5"</c> for
/// PartType.Elbow). The empty string is the documented sentinel:
/// the fact was evaluated at extraction time but the source parameter was
/// absent in the family — detection treats the fact as known-missing and
/// the UI hides the row.</param>
/// <param name="ValueDisplay">Human-readable fallback captured at
/// extraction time (enum member name, e.g. <c>"Elbow"</c>). The UI prefers
/// its own localized label map (<see cref="PartTypeLabelMap"/>) and falls
/// back to this value for unknown keys.</param>
public sealed record FamilyFact(
    string FactKey,
    string ValueKey,
    string ValueDisplay);

/// <summary>
/// Read model for the properties window: the item's Revit category ordinal
/// plus all extracted facts, loaded in one repository call
/// (<see cref="SmartCon.Core.Services.Interfaces.IFamilyFactRepository"/>).
/// </summary>
/// <param name="RevitCategoryId">The <c>BuiltInCategory</c> ordinal stored
/// in <c>catalog_items.revit_category_id</c>, or <c>null</c> when the item
/// was imported before the facts subsystem existed (V22) and has not been
/// actualized yet.</param>
/// <param name="Facts">Facts extracted for the item (possibly empty).</param>
public sealed record FamilyFactsData(
    int? RevitCategoryId,
    IReadOnlyList<FamilyFact> Facts);
