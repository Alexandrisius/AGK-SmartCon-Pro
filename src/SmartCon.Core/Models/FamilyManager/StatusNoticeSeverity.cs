namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Severity of a status notice. Declaration order matters: the worst severity
/// of a notice list is computed as <c>Max()</c>. Lives in Core so both the
/// FamilyManager view-models and SmartCon.UI / XAML can reference it without a
/// WPF local-type markup compile pass.
/// </summary>
public enum StatusNoticeSeverity
{
    /// <summary>Neutral fact (dependency-of, marker-resolved version).</summary>
    Info,

    /// <summary>Something needs attention but does not block the workflow.</summary>
    Warning,

    /// <summary>Blocking problem (import block, failed dependencies).</summary>
    Error,
}