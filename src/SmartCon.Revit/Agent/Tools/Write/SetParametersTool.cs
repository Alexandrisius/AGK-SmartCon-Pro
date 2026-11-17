using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class SetParametersTool : AgentWriteToolBase
{
    private const int MaxItems = 500;

    public SetParametersTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_parameters",
        Description =
            "Writes parameter values. items: [{elementId, parameter?, guid?, value, scope?, unit?}]. " +
            "parameter is a name, a BuiltInParameter id (ALL_MODEL_INSTANCE_COMMENTS) or a shared " +
            "parameter GUID. scope: \"instance\" (default) or \"type\" (changes every instance of " +
            "the type). Numeric values need unit=\"mm\" (or \"m\", \"ft\"). Read-only and " +
            "formula parameters are refused. dryRun defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":500,""items"":{""type"":""object"",""properties"":{""elementId"":{""type"":""integer""},""parameter"":{""type"":""string""},""guid"":{""type"":""string""},""value"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""unit"":{""type"":""string""}},""required"":[""elementId"",""value""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<SetParametersRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "no_items";
            outcome.Message = "The items array is empty; pass at least one parameter write.";
            return;
        }

        if (items.Count > MaxItems)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxItems + " parameter writes per call are allowed.";
            return;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        outcome.Description = "Set " + items.Count + " parameter value(s)";

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.ElementId, Name = item.Parameter };
            var element = doc.GetElement(ElementIdCompat.Create(item.ElementId));
            if (element is null)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "element_not_found",
                    "Element " + item.ElementId + " does not exist in the active document.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var resolution = AgentParameterWriteHelper.Resolve(
                doc, element, item.Parameter, item.Guid, item.Scope);
            if (!resolution.Ok)
            {
                ApplyViewTemplateTool.Fail(outcome, report, resolution.ErrorCode!, resolution.Reason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var parameter = resolution.Parameter!;
            var scopeKey = resolution.IsTypeScope ? "type:" : "instance:";
            var parameterName = parameter.Definition?.Name ?? item.Parameter ?? item.Guid ?? "?";
            report.Name = scopeKey + parameterName;

            var before = AgentParameterWriteHelper.Describe(parameter);
            var rawBefore = AgentParameterWriteHelper.RawValue(parameter);

            if (!AgentParameterWriteHelper.TrySet(
                    parameter, item.Value, item.Unit, out var errorCode, out var reason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, errorCode, reason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var after = AgentParameterWriteHelper.Describe(parameter);
            var rawAfter = AgentParameterWriteHelper.RawValue(parameter);
            report.OldValue = before;
            report.NewValue = after;
            report.Detail = (resolution.IsTypeScope ? "type" : "instance") + " parameter";

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                report.Reason = "The parameter already has this value (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;

            outcome.Items.Add(new ChangeItem
            {
                UniqueId = element.UniqueId,
                ElementId = element.Id.GetValue(),
                ParameterName = scopeKey + parameterName,
                OldValue = rawBefore,
                NewValue = rawAfter,
                Status = ChangeStatus.Applied
            });
        }
    }

    private sealed class SetParametersRequest
    {
        public List<ParameterWriteItem>? Items { get; set; }
    }
}
