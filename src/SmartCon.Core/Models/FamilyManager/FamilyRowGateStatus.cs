namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Combined validation-gate status of a batch import row, shown in the
/// dialog's status column BEFORE the import runs (while
/// <see cref="FamilyBatchImportRowState"/> stays Pending).
/// </summary>
public enum FamilyRowGateStatus
{
    /// <summary>Category not assigned yet — rule check has not run; only
    /// the health result (if any) is known.</summary>
    NotChecked = 0,

    /// <summary>Rule check is running (async, after category assignment).</summary>
    Checking = 1,

    /// <summary>Health OK and every rule passed (or no rules configured
    /// for the assigned category).</summary>
    Passed = 2,

    /// <summary>Health OK, rules passed/absent, but the family has
    /// warning-severity health issues. Import is allowed.</summary>
    Warning = 3,

    /// <summary>Health errors or rule violations — the row is blocked
    /// (forced Skip, hard gate).</summary>
    Failed = 4,
}
