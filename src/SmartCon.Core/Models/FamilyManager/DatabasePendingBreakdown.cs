namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Pending actualization records split by tier and openability (ADR-054).
/// <see cref="Critical"/>/<see cref="Optional"/> are PROCESSABLE groups
/// (drive the banner/gate and the menu command);
/// <see cref="NewerOnlyCritical"/> groups need a newer Revit and GATE just
/// like processable critical (§3a — the database stays read-only until
/// perfectly updated); <see cref="NewerOnlyOptional"/> groups drive only
/// the non-blocking amber indicator.
/// </summary>
public sealed record DatabasePendingBreakdown(
    int Critical,
    int Optional,
    int NewerOnlyCritical,
    int NewerOnlyOptional,
    int NewerOnlyCriticalRequiredRevitVersion,
    int NewerOnlyOptionalRequiredRevitVersion)
{
    public static DatabasePendingBreakdown Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Processable records of any tier (the unified command's workload).</summary>
    public int TotalProcessable => Critical + Optional;

    /// <summary>
    /// Critical records of any openability — the gate condition (§3a:
    /// read-only until the database is perfectly updated).
    /// </summary>
    public int TotalCritical => Critical + NewerOnlyCritical;
}
