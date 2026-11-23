using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Writes view parameters by name or parameterId — the browser-organization
/// parameters (Назначение вида, Раздел, параметр имени вида в штампе) and any other
/// instance parameter of a view or sheet.
/// </summary>
public sealed class SetViewParametersTool : AgentWriteToolBase
{
    private const int MaxViews = 50;
    private const int MaxParameters = 20;

    public SetViewParametersTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_view_parameters",
        Description =
            "Sets instance parameters of views or sheets: viewIds plus parameters[{name|" +
            "parameterId, value, unit}]. Use it for the project-browser parameters (Назначение " +
            "вида, Раздел, параметр имени вида в штампе) and any other view parameter. Read current " +
            "values with get_element_details (elementIds=[viewId]) or list_views " +
            "(browserFolderParameters shows which parameters group the browser). A view template id " +
            "is accepted too: a template is a View, so its own parameters are written directly. " +
            "A parameter " +
            "controlled by a view template is refused with view_controlled_by_template: detach " +
            "the template first (apply_view_template with templateId=0). scale/масштаб resolves " +
            "the view scale built-in (a 3D view displays it as «Масштаб вида»), detail level and " +
            "discipline have the same aliases. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""parameters"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""parameterId"":{""type"":""integer""},""value"":{""type"":""string""},""unit"":{""type"":""string""}},""required"":[""value""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""viewIds"",""parameters""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        if (viewIds.Count == 0 || viewIds.Count > MaxViews)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "viewIds must contain 1.." + MaxViews + " ids.";
            return;
        }

        var specs = ReadSpecs(args, outcome);
        if (specs.Count == 0 || specs.Count > MaxParameters)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "parameters must contain 1.." + MaxParameters + " items with value.";
            return;
        }

        var changed = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var viewId in viewIds)
        {
            ct.ThrowIfCancellationRequested();

            if (doc.GetElement(SmartCon.Core.Compatibility.ElementIdCompat.Create(viewId))
                is not View view)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = viewId,
                    ErrorCode = "view_not_found",
                    Reason = "No view with id " + viewId + " exists."
                });
                failed++;
                continue;
            }

            foreach (var spec in specs)
            {
                var item = new WriteItemReport { Id = viewId, Name = view.Name };

                var parameter = ResolveParameter(doc, view, spec, out var resolveCode, out var resolveReason);
                if (parameter is null)
                {
                    item.ErrorCode = resolveCode ?? "parameter_not_found";
                    item.Reason = resolveReason
                                  ?? "Parameter '" + (spec.Name ?? "id " + spec.ParameterId) +
                                  "' was not found on view '" + view.Name + "'.";
                    outcome.Batch.Failed.Add(item);
                    failed++;
                    continue;
                }

                item.Name = parameter.Definition?.Name ?? item.Name;

                if (parameter.IsReadOnly && view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    item.ErrorCode = "view_controlled_by_template";
                    item.Reason = "Parameter '" + item.Name + "' is controlled by view template " +
                                  view.ViewTemplateId.GetValue() +
                                  "; detach the template first (apply_view_template with templateId=0).";
                    item.SuggestedViewIds = new List<long> { view.ViewTemplateId.GetValue() };
                    outcome.Batch.Failed.Add(item);
                    failed++;
                    continue;
                }

                var oldDisplay = AgentParameterWriteHelper.Describe(parameter);
                if (!AgentParameterWriteHelper.TrySet(
                        parameter, spec.Value, spec.Unit, out var code, out var reason))
                {
                    item.ErrorCode = code;
                    item.Reason = reason;
                    outcome.Batch.Failed.Add(item);
                    failed++;
                    continue;
                }

                var newDisplay = AgentParameterWriteHelper.Describe(parameter);
                if (string.Equals(oldDisplay, newDisplay, StringComparison.Ordinal))
                {
                    item.Reason = "The parameter already has this value.";
                    outcome.Batch.Skipped.Add(item);
                    skipped++;
                    continue;
                }

                item.OldValue = oldDisplay;
                item.NewValue = newDisplay;
                outcome.Batch.Changed.Add(item);
                changed++;

                outcome.Items.Add(new ChangeItem
                {
                    ElementId = view.Id.GetValue(),
                    UniqueId = view.UniqueId,
                    ParameterName = item.Name,
                    OldValue = oldDisplay,
                    NewValue = newDisplay,
                    Status = ChangeStatus.Applied
                });
            }
        }

        outcome.AffectedCount = changed;
        // The change items are recorded under the view parameter's own name, which the
        // change-set replayer does not know: undo_last cannot revert them. Report that
        // honestly instead of returning an undoable-looking change set.
        if (changed > 0)
        {
            // No change set: its items cannot be replayed, and a set that undo_last
            // closes with "nothing_to_undo" also hides the previous set from it.
            outcome.SuppressChangeSet = true;
            outcome.Data["undoSupported"] = false;
            outcome.Warnings.Add(
                "undo_last does not revert view parameter changes (undoSupported=false); " +
                "to revert, call set_view_parameters again with the previous values from " +
                "changed[].oldValue.");
        }

        if (failed > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = changed + " parameter(s) set, " + skipped + " unchanged, " +
                              failed + " failed. See failed[].";
        }
        else if (changed == 0)
        {
            outcome.Message = "No changes: every requested parameter already has the value.";
        }
    }

    private static List<ViewParameterSpec> ReadSpecs(JsonElement args, WriteOutcome outcome)
    {
        var result = new List<ViewParameterSpec>();
        if (!args.TryGetProperty("parameters", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_arguments";
                outcome.Message = "Each parameters[] item must be an object.";
                return result;
            }

            var name = AgentJson.GetString(item, "name");
            var parameterId = AgentJson.GetLong(item, "parameterId", 0);
            var value = AgentJson.GetString(item, "value");
            // Built-in parameter ids are negative (ElementId.InvalidElementId is -1),
            // so 0 is the only "not provided" sentinel.
            if (string.IsNullOrWhiteSpace(name) && parameterId == 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_arguments";
                outcome.Message = "Each parameter needs a name or parameterId.";
                return result;
            }

            if (value is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_arguments";
                outcome.Message = "Each parameter needs a string value.";
                return result;
            }

            result.Add(new ViewParameterSpec
            {
                Name = name,
                ParameterId = parameterId != 0 ? parameterId : null,
                Value = value,
                Unit = AgentJson.GetString(item, "unit")
            });
        }

        return result;
    }

    private static Parameter? ResolveParameter(
        Document doc,
        View view,
        ViewParameterSpec spec,
        out string? errorCode,
        out string? reason)
    {
        errorCode = null;
        reason = null;

        if (spec.ParameterId.HasValue)
        {
            foreach (Parameter candidate in view.Parameters)
            {
                if (candidate.Id.GetValue() == spec.ParameterId.Value)
                    return candidate;
            }

            return null;
        }

        var resolution = AgentParameterWriteHelper.Resolve(
            doc, view, spec.Name, guid: null, scope: "instance");
        if (!resolution.Ok)
        {
            errorCode = resolution.ErrorCode;
            reason = resolution.Reason;
        }

        return resolution.Parameter;
    }

    private sealed class ViewParameterSpec
    {
        public string? Name { get; init; }

        public long? ParameterId { get; init; }

        public required string Value { get; init; }

        public string? Unit { get; init; }
    }
}