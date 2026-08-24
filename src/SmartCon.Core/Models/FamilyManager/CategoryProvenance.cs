namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Issue #135: describes WHERE the target category of a batch-import row
/// came from. This is the single source of truth for the "category lock"
/// semantics: locked provenances (<see cref="Command"/>,
/// <see cref="Manual"/>) survive a row rename untouched; automatic
/// provenances (<see cref="AutoName"/>, <see cref="AutoHash"/>,
/// <see cref="None"/>) are re-derived by the rename handler.
/// </summary>
public enum CategoryProvenance
{
    /// <summary>No category assigned («Без категории» placeholder).</summary>
    None = 0,

    /// <summary>
    /// Pulled from the catalog by normalized-name match (the row's file
    /// name equals an existing catalog item). Re-derived on rename.
    /// </summary>
    AutoName = 1,

    /// <summary>
    /// Pulled from a hash-matched duplicate (ADR-049: content identity,
    /// name may differ). Re-derived on rename.
    /// </summary>
    AutoHash = 2,

    /// <summary>
    /// Preselected by the «Импорт в категорию» command. Locked: rename
    /// never resets it; renaming to an existing family moves that family
    /// into this category on import.
    /// </summary>
    Command = 3,

    /// <summary>
    /// Explicitly picked by the user (category picker or multi-select
    /// batch apply). Locked: rename never resets it.
    /// </summary>
    Manual = 4,

    /// <summary>
    /// Assigned by an auto-assignment rule (#241). Automatic provenance:
    /// the rename handler re-derives it (re-evaluates the rules with the
    /// row's current name); a manual pick replaces it.
    /// </summary>
    AutoRule = 5,
}
