using System.Text.Json.Serialization;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Spec, unit-argument, state and undo models of the scope action.
/// </summary>
public sealed partial class SetFamilyParameterScopeJobAction
{
    internal sealed class SetScopeSpec
    {
        public SetScopeSelectSpec? Select { get; set; }

        /// <summary>
        /// Named parameterRef (NOT "parameter"): the shared start_job schema
        /// already types spec.parameter as a string for set_parameters, so an
        /// object under that key is rejected by schema validation before the
        /// action ever sees it (live agent run, 26.11).
        /// </summary>
        [JsonPropertyName("parameterRef")]
        public SetScopeParameterRef? ParameterRef { get; set; }

        public bool? IsInstance { get; set; }
    }

    internal sealed class SetScopeSelectSpec
    {
        public List<long>? FamilyIds { get; set; }

        public List<string>? FamilyUniqueIds { get; set; }

        [JsonPropertyName("familyNames")]
        public List<string>? FamilyNames { get; set; }

        public string? NameContains { get; set; }

        public List<long>? CategoryIds { get; set; }

        public int? Limit { get; set; }
    }

    internal sealed class SetScopeParameterRef
    {
        public string? Name { get; set; }

        [JsonPropertyName("guid")]
        public string? SharedParameterGuid { get; set; }
    }

    private sealed class SetScopeUnitArgs
    {
        public long FamilyId { get; set; }

        public string? FamilyUniqueId { get; set; }

        public string FamilyName { get; set; } = string.Empty;

        [JsonPropertyName("parameterRef")]
        public SetScopeParameterRef? ParameterRef { get; set; }

        public bool IsInstance { get; set; }
    }

    private sealed class SetScopeEntryState
    {
        public string Name { get; set; } = string.Empty;

        public string Scope { get; set; } = "type";
    }

    private sealed class SetScopeUndoPlan
    {
        public string? FamilyName { get; set; }

        public string? BackupPath { get; set; }

        public string? OriginalScope { get; set; }

        public string? NewFamilyUniqueId { get; set; }
    }
}
