namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Verdict of a catalog compliance check (#259): one catalog item against
/// the effective validation rules of its category. Deliberately separate
/// from <see cref="StaleReason"/> — staleness compares the PROJECT family
/// with the catalog, compliance compares the CATALOG item with the rules;
/// the two verdicts never mix (the «Обновить» button cannot fix a rule
/// violation, so a violation must not appear as a stale reason).
/// </summary>
public enum ComplianceStatus
{
    /// <summary>Not checked yet in this session (tree-node default; never
    /// stored in a <see cref="CatalogComplianceSnapshot"/>).</summary>
    NotChecked = 0,

    /// <summary>No violations (including categories without rules — such
    /// items pass freely, mirroring the import gate semantics).</summary>
    Pass,

    /// <summary>At least one rule violation — the fix is to edit the family
    /// and import a new version (the import gate revalidates on entry).</summary>
    Fail,

    /// <summary>The item has no extraction data (no import run / no
    /// extracted attribute values) — the rules cannot be evaluated.
    /// Guidance: run «Обновить базу» to backfill the attributes.</summary>
    CannotVerify,
}
