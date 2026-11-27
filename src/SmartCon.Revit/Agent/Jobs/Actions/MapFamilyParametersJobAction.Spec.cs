using System.Text.Json.Serialization;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Spec, unit-argument, preview-state and plan models of the family mapping
/// action (extracted from the core file to keep every partial under the size cap).
/// </summary>
public sealed partial class MapFamilyParametersJobAction
{
    public sealed class MapFamilyParametersSpec
    {
        public MapFamilySelectSpec? Select { get; set; }

        public List<MapFamilyMappingSpec>? Mappings { get; set; }
    }

    public sealed class MapFamilySelectSpec
    {
        public List<long>? FamilyIds { get; set; }

        public List<string>? FamilyUniqueIds { get; set; }

        [JsonPropertyName("familyNames")]
        public List<string>? FamilyNames { get; set; }

        public string? NameContains { get; set; }

        public List<long>? CategoryIds { get; set; }

        /// <summary>Category names (display/OST_*/id) — resolved like set_parameters (v16 D3).</summary>
        [JsonPropertyName("categoryNames")]
        public List<string>? CategoryNames { get; set; }

        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }

        public int? Limit { get; set; }
    }

    public sealed class MapFamilyMappingSpec
    {
        public MapFamilyParameterRef? Source { get; set; }

        public MapFamilyParameterRef? Target { get; set; }

        public string? Mode { get; set; }

        public List<string>? TypeNames { get; set; }

        /// <summary>Create the target parameter from a shared definition when it is missing (#431).</summary>
        [JsonPropertyName("createTarget")]
        public bool CreateTarget { get; set; }
    }

    public sealed class MapFamilyParameterRef
    {
        public string? Name { get; set; }

        public string? Scope { get; set; }

        [JsonPropertyName("guid")]
        public string? SharedParameterGuid { get; set; }
    }

    private sealed class MapFamilyUnitArgs
    {
        public long FamilyId { get; set; }

        public string? FamilyUniqueId { get; set; }

        public string FamilyName { get; set; } = string.Empty;

        public List<MapFamilyMappingSpec>? Mappings { get; set; }

        public string? Error { get; set; }
    }

    private sealed class FamilyMapState
    {
        public List<FamilyMapTypeState> Types { get; init; } = new();

        /// <summary>Targets this unit would create when createTarget mappings are present.</summary>
        public List<FamilyMapCreateState>? WouldCreateTargets { get; set; }
    }

    private sealed class FamilyMapTypeState
    {
        public string TypeName { get; init; } = string.Empty;

        public List<FamilyMapValueState> Targets { get; init; } = new();
    }

    private sealed class FamilyMapValueState
    {
        public string Name { get; init; } = string.Empty;

        public string Scope { get; init; } = string.Empty;

        public string Storage { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;
    }

    /// <summary>Preview marker of a target parameter the apply would create (#431).</summary>
    private sealed class FamilyMapCreateState
    {
        public string Name { get; init; } = string.Empty;

        public string Scope { get; init; } = string.Empty;

        public string Spec { get; init; } = string.Empty;
    }

    private sealed class FamilyMapUndoPlan
    {
        public string? FamilyName { get; set; }

        public string? BackupPath { get; set; }

        public string? NewFamilyUniqueId { get; set; }

        public List<FamilyUndoOperation> Ops { get; set; } = new();
    }

    private sealed class MapPlanEntry
    {
        public required MapFamilyParameterRef Source { get; init; }

        public required MapFamilyParameterRef Target { get; init; }

        public required string SourceName { get; init; }

        public required string TargetName { get; init; }

        public required string Scope { get; init; }

        public required string Storage { get; init; }

        public required string Mode { get; init; }

        public required HashSet<string> TypeNames { get; init; }

        public string? FormulaToApply { get; init; }

        public bool ClearTargetFormula { get; init; }

        /// <summary>The target parameter does not exist yet and must be created at apply time.</summary>
        public bool CreateTarget { get; init; }

        public bool TargetIsInstance { get; init; }
    }

    private sealed class FamilyMapPlan
    {
        public List<MapPlanEntry> Entries { get; } = new();

        public List<FamilyUndoOperation> UndoOps { get; } = new();

        public List<FamilyMapCreateState> Creates { get; } = new();

        public FamilyMapState Expected { get; set; } = new();

        public FamilyMapState Actual { get; set; } = new();

        public int ChangeCount { get; set; }
    }
}
