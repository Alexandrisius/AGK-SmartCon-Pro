using System.Text.Json.Serialization;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Spec, unit-argument, plan and undo models of the replace action (extracted
/// from the core file to keep every partial under the size cap).
/// </summary>
public sealed partial class ReplaceFamilyParameterJobAction
{
    public sealed class ReplaceFamilyParametersSpec
    {
        public ReplaceFamilySelectSpec? Select { get; set; }

        public List<ReplaceFamilyReplacementSpec>? Replacements { get; set; }
    }

    public sealed class ReplaceFamilySelectSpec
    {
        public List<long>? FamilyIds { get; set; }

        public List<string>? FamilyUniqueIds { get; set; }

        [JsonPropertyName("familyNames")]
        public List<string>? FamilyNames { get; set; }

        public string? NameContains { get; set; }

        public List<long>? CategoryIds { get; set; }

        public int? Limit { get; set; }
    }

    public sealed class ReplaceFamilyReplacementSpec
    {
        public ReplaceFamilyRefSpec? Source { get; set; }

        public ReplaceFamilyRefSpec? Target { get; set; }
    }

    /// <summary>
    /// Parameter reference by name and/or shared-parameter GUID. The scope is not
    /// part of the reference: a replacement inherits the scope of the source
    /// parameter (ReplaceParameter keeps values/formulas only in the same scope).
    /// </summary>
    public sealed class ReplaceFamilyRefSpec
    {
        public string? Name { get; set; }

        [JsonPropertyName("guid")]
        public string? SharedParameterGuid { get; set; }
    }

    private sealed class ReplaceFamilyUnitArgs
    {
        public long FamilyId { get; set; }

        public string? FamilyUniqueId { get; set; }

        public string FamilyName { get; set; } = string.Empty;

        public List<ReplaceFamilyReplacementSpec>? Replacements { get; set; }

        public string? Error { get; set; }
    }

    /// <summary>
    /// FamilyMapUndoPlan-compatible undo payload: the ops list stays empty (a
    /// parameter replacement is not revertible value-by-value), so the pre-edit
    /// .rfa backup is the rollback path (J-04).
    /// </summary>
    private sealed class FamilyReplaceUndoPlan
    {
        public string? FamilyName { get; set; }

        public string? BackupPath { get; set; }

        public string? NewFamilyUniqueId { get; set; }

        public List<FamilyUndoOperation> Ops { get; set; } = new();
    }

    private sealed class ReplacePlanEntry
    {
        public required ReplaceFamilyRefSpec Source { get; init; }

        public required ReplaceFamilyRefSpec Target { get; init; }

        public required string SourceName { get; init; }

        public required string TargetName { get; init; }

        public required Guid TargetGuid { get; init; }

        public required Autodesk.Revit.DB.ExternalDefinition TargetDefinition { get; init; }

        public required Autodesk.Revit.DB.ForgeTypeId GroupTypeId { get; init; }

        public required bool IsInstance { get; init; }

        public required string Storage { get; init; }

        public bool AlreadyOnTarget { get; init; }

        public List<string> FormulaDependents { get; init; } = new();

        /// <summary>Per-type raw values of the source parameter captured before the replace (verify input).</summary>
        public List<ReplaceTypeSnapshot> TypeSnapshot { get; init; } = new();
    }

    private sealed class ReplaceTypeSnapshot
    {
        public required string TypeName { get; init; }

        public required string Storage { get; init; }

        public required string Value { get; init; }
    }

    private sealed class FamilyReplacePlan
    {
        public List<ReplacePlanEntry> Entries { get; } = new();

        public List<string> Warnings { get; } = new();

        public List<FamilyReplaceEntryState> Expected { get; } = new();

        public List<FamilyReplaceActualState> Actual { get; } = new();

        public int ChangeCount { get; set; }
    }

    /// <summary>One replacement as exposed in the preview ExpectedJson.</summary>
    public sealed class FamilyReplaceEntryState
    {
        public FamilyReplaceSourceState? Source { get; set; }

        public FamilyReplaceTargetState? Target { get; set; }

        public List<string> FormulasAffected { get; set; } = new();

        public bool AlreadyOnTarget { get; set; }

        public List<FamilyReplaceTypeState> Types { get; set; } = new();
    }

    public sealed class FamilyReplaceSourceState
    {
        public string Name { get; set; } = string.Empty;

        public string? Guid { get; set; }

        public string Scope { get; set; } = string.Empty;

        public bool IsShared { get; set; }
    }

    public sealed class FamilyReplaceTargetState
    {
        public string Name { get; set; } = string.Empty;

        public string? Guid { get; set; }
    }

    public sealed class FamilyReplaceTypeState
    {
        public string TypeName { get; set; } = string.Empty;

        public string Storage { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Current binding of the source parameter (the preview ActualJson).</summary>
    public sealed class FamilyReplaceActualState
    {
        public string Name { get; set; } = string.Empty;

        public string? Guid { get; set; }

        public string Scope { get; set; } = string.Empty;

        public bool IsShared { get; set; }
    }
}
