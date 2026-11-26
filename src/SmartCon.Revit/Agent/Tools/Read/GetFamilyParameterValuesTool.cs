using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Narrow read-back verifier for ONE family parameter: the formula and the
/// value on every family type, without loading the rest of the family. The
/// migration loop (add a duplicate → set an authored formula → verify per
/// type) must not pay the full get_family_parameters(includeValues=true)
/// price on every step — the ФОП session lost two calls to that timeout.
/// </summary>
public sealed class GetFamilyParameterValuesTool : AgentReadToolBase
{
    private const int MaxTypes = 200;

    public GetFamilyParameterValuesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_family_parameter_values",
        Description =
            "Reads ONE family parameter across every family type: current formula, storage " +
            "type, shared GUID, isInstance and the evaluated value per type. The cheap " +
            "read-back after set_family_formula / add_family_parameter / a migration job — " +
            "instead of re-reading the whole parameter list of the family " +
            "(get_family_parameters with includeValues). Types are capped at " + MaxTypes +
            " (typesTruncated marks the cut); instance parameters report the type default " +
            "the same way the Family Editor shows it.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""parameter"":{""type"":""string"",""description"":""Parameter name (or use guid)""},""guid"":{""type"":""string"",""description"":""Shared parameter GUID — the safe key when several same-name parameters exist""},""scope"":{""type"":""string"",""enum"":[""all"",""instance"",""type""],""default"":""all""},""typeNames"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":50,""description"":""Optional filter: values for these types only (default: every type up to the cap)""}},""required"":[],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var family = AgentFamilyReader.ResolveFamily(
            doc,
            TryGetLong(args, "familyId"),
            AgentJson.GetString(args, "familyName"),
            AgentJson.GetString(args, "familyUniqueId"),
            out var errorCode,
            out var message);
        if (family is null)
            return ToolResult.Fail(errorCode ?? "family_not_found", message ?? "Family not found.");

        if (!AgentFamilyReader.TryOpenFamilyDocument(
                doc,
                family,
                out var familyDoc,
                out var openErrorCode,
                out var openMessage))
        {
            return ToolResult.Fail(
                openErrorCode ?? "family_open_failed",
                openMessage ?? "The family document could not be opened.");
        }

        try
        {
            var scopeText = AgentJson.GetString(args, "scope");
            if (!string.IsNullOrWhiteSpace(scopeText)
                && !string.Equals(scopeText, "all", StringComparison.OrdinalIgnoreCase))
            {
                var wantInstance = AgentFamilyReader.ParseScope(scopeText, out var scopeMessage);
                if (wantInstance is null)
                    return ToolResult.Fail("invalid_arguments", scopeMessage ?? "scope must be all, instance or type.");
                scopeText = wantInstance.Value ? "instance" : "type";
            }
            else
            {
                // "all" (the schema default) must not reach FindParameter: its
                // ParseScope rejects it. null keeps the name/guid match unscoped.
                scopeText = null;
            }

            var manager = familyDoc!.FamilyManager;
            var parameter = AgentFamilyReader.FindParameter(
                manager,
                AgentJson.GetString(args, "parameter"),
                AgentJson.GetString(args, "guid"),
                scopeText,
                out var findErrorCode,
                out var findMessage);
            if (parameter is null)
                return ToolResult.Fail(
                    findErrorCode ?? "parameter_not_found",
                    findMessage ?? "Parameter not found. Pass parameter (name) or guid.");

            var typeFilter = AgentJson.GetStringArray(args, "typeNames");
            var types = AgentFamilyReader.GetTypes(manager);
            var selected = new List<FamilyType>();
            var matchedTypes = 0;
            foreach (var type in types)
            {
                ct.ThrowIfCancellationRequested();
                if (typeFilter.Count > 0
                    && !typeFilter.Contains(type.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                matchedTypes++;
                if (selected.Count >= MaxTypes)
                    continue;
                selected.Add(type);
            }

            var values = new List<Dictionary<string, object?>>(selected.Count);
            foreach (var type in selected)
            {
                ct.ThrowIfCancellationRequested();
                values.Add(new Dictionary<string, object?>
                {
                    ["typeName"] = type.Name,
                    ["value"] = AgentFamilyReader.DisplayValue(type, parameter)
                });
            }

            var payload = new Dictionary<string, object?>
            {
                ["familyId"] = family.Id.GetValue(),
                ["familyName"] = family.Name,
                ["parameter"] = parameter.Definition.Name,
                ["guid"] = parameter.IsShared ? parameter.GUID.ToString("D") : null,
                ["isShared"] = parameter.IsShared,
                ["isInstance"] = parameter.IsInstance,
                ["storageType"] = AgentFamilySpecs.StorageTypeName(parameter.StorageType),
                ["formula"] = parameter.Formula,
                ["isDeterminedByFormula"] = parameter.IsDeterminedByFormula,
                ["typeCount"] = types.Count,
                ["typesMatched"] = matchedTypes,
                ["typesReturned"] = values.Count,
                ["typesTruncated"] = matchedTypes > values.Count,
                ["values"] = values
            };

            var messageText = "Parameter '" + parameter.Definition.Name + "' of '" + family.Name +
                              "': " + values.Count + " of " + types.Count + " type(s), formula " +
                              (string.IsNullOrEmpty(parameter.Formula) ? "(none)" : "'" + parameter.Formula + "'") +
                              (types.Count > values.Count ? " (types capped at " + MaxTypes + ")" : string.Empty) + ".";
            return ToolResult.Ok(AgentJson.Serialize(payload), messageText);
        }
        finally
        {
            AgentFamilyDocCloser.Close(familyDoc);
        }
    }

    private static long? TryGetLong(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            return null;
        }

        return result;
    }
}
