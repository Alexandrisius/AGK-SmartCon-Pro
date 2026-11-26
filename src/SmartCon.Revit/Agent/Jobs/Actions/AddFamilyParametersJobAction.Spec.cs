using System.Text.Json.Serialization;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Spec, unit-argument, plan and undo models of the add action (extracted to
/// keep every partial under the size cap).
/// </summary>
public sealed partial class AddFamilyParametersJobAction
{
    internal sealed class AddFamilyParametersSpec
    {
        public AddFamilySelectSpec? Select { get; set; }

        public List<AddFamilyParameterRequest>? Parameters { get; set; }
    }

    internal sealed class AddFamilySelectSpec
    {
        public List<long>? FamilyIds { get; set; }

        public List<string>? FamilyUniqueIds { get; set; }

        [JsonPropertyName("familyNames")]
        public List<string>? FamilyNames { get; set; }

        public string? NameContains { get; set; }

        public List<long>? CategoryIds { get; set; }

        public int? Limit { get; set; }
    }

    private sealed class AddFamilyUnitArgs
    {
        public long FamilyId { get; set; }

        public string? FamilyUniqueId { get; set; }

        public string FamilyName { get; set; } = string.Empty;

        public List<AddFamilyParameterRequest>? Parameters { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>
    /// One parameter of the per-family plan: an add, or a skip with the reason
    /// (the name already exists — the migration idempotency case).
    /// </summary>
    private sealed class AddFamilyPlanEntry
    {
        public string Name { get; set; } = string.Empty;

        public string? StorageType { get; set; }

        public string? Group { get; set; }

        public bool IsInstance { get; set; }

        public bool IsShared { get; set; }

        public string? Formula { get; set; }

        public string Action { get; set; } = "add";

        public string? Reason { get; set; }
    }

    private sealed class AddFamilyPlan
    {
        public List<AddFamilyPlanEntry> Entries { get; } = new();

        public int ChangeCount { get; set; }

        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Expected/verification state per parameter: the read-back proves the
    /// parameter exists on the family types with the formula applied.
    /// </summary>
    private sealed class AddFamilyEntryState
    {
        public string Name { get; set; } = string.Empty;

        public string Scope { get; set; } = "type";

        public string? Formula { get; set; }
    }

    /// <summary>
    /// Backup-based undo: the pre-edit .rfa is the honest rollback path (a
    /// reload changes family ids, so value replay is not).
    /// </summary>
    private sealed class AddFamilyUndoPlan
    {
        public string? FamilyName { get; set; }

        public string? BackupPath { get; set; }

        public string? NewFamilyUniqueId { get; set; }
    }
}
