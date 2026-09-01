namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of a loadable embedded-vs-file content verification with
/// per-type resolution (Issue #249, Phase 2). <see cref="Verdict"/> keeps
/// the family-level tri-state semantics of the legacy boolean
/// verification (<c>null</c> = indeterminate — never evidence).
/// <see cref="PerTypeStale"/> resolves WHICH loaded types drifted:
/// original type name (display-ready) → isStale, the dictionary compares
/// ordinal-ignore-case. <c>null</c> when the verification was
/// indeterminate — callers fall back to the family-level verdict in that
/// case.
/// </summary>
/// <param name="Verdict">Family-level content verdict: <c>true</c> —
/// matches; <c>false</c> — differs; <c>null</c> — indeterminate.</param>
/// <param name="PerTypeStale">
/// Per-type drift map over the compared type set: in a PROJECT the
/// intersection of embedded and file types (the type-set rule — a
/// preserve-types reload keeps the project's loaded subset, so
/// locally-added types are NOT drift); in a FAMILY DOCUMENT the union,
/// with one-sided types reported as stale (the doc-to-doc merge
/// transfers the whole type set). Computed even when the family-level
/// verdict is a match (all-false map) so the UI can clear stale dots
/// per type.
/// </param>
public sealed record LoadableVerificationResult(
    bool? Verdict,
    IReadOnlyDictionary<string, bool>? PerTypeStale);
