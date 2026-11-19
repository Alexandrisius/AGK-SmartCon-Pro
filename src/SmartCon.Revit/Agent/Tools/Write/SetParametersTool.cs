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
            "the type). Numeric values REQUIRE unit: \"mm\" (or \"m\", \"ft\", \"m2\", \"m3\"), " +
            "or \"raw\" for internal Revit units. Read-only and formula parameters are refused. " +
            "dryRun defaults to true.",
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
        var typeIds = new HashSet<long>();

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

            if (string.Equals(rawBefore, rawAfter, StringComparison.Ordinal))
            {
                report.Reason = "The parameter already has this value (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;

            if (resolution.IsTypeScope && parameter.Element is not null)
                typeIds.Add(parameter.Element.Id.GetValue());

            // A type-scope write belongs to the type element; storing the instance id would
            // break undo when the instance is deleted or retyped.
            var owner = resolution.IsTypeScope && parameter.Element is not null
                ? parameter.Element
                : element;

            outcome.Items.Add(new ChangeItem
            {
                UniqueId = owner.UniqueId,
                ElementId = owner.Id.GetValue(),
                ParameterName = scopeKey + parameterName,
                OldValue = rawBefore,
                NewValue = rawAfter,
                Status = ChangeStatus.Applied
            });
        }

        if (typeIds.Count > 0)
            DescribeBlastRadius(doc, typeIds, outcome);
    }

    private static void DescribeBlastRadius(
        Document doc,
        HashSet<long> typeIds,
        WriteOutcome outcome)
    {
        var affected = 0;
        var samples = new List<long>();
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            var typeId = element.GetTypeId();
            if (typeId == ElementId.InvalidElementId)
                continue;

            if (!typeIds.Contains(typeId.GetValue()))
                continue;

            affected++;
            if (samples.Count < 10)
                samples.Add(element.Id.GetValue());
        }

        outcome.Data["blastRadius"] = new
        {
            affectedTypes = typeIds.Count,
            affectedInstances = affected,
            sampleInstanceIds = samples,
            countCapped = false,
            undoReturnsAllInstances = true,
            familyReload = false,
            note = "Direct instances only; parts, assemblies and linked models are not counted."
        };
        outcome.Warnings.Add(
            "Type-scope write: " + affected + " instance(s) of " + typeIds.Count +
            " type(s) change; undo_last reverts all of them.");
    }

    private sealed class SetParametersRequest
    {
        public List<ParameterWriteItem>? Items { get; set; }
    }
}
