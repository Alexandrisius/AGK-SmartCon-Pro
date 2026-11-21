using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Switches elements to another type in one bulk call: the static
/// <see cref="Element.ChangeTypeId(Document, ICollection{ElementId}, ElementId)"/>
/// changes the whole set with a single regeneration. The target may be a
/// family type or a system type (walls, floors, ducts); elements come either
/// as an explicit elementIds list or as every element of fromTypeIds.
/// </summary>
public sealed class ReplaceFamilyTypeTool : AgentWriteToolBase
{
    private const int MaxElements = 2000;

    public ReplaceFamilyTypeTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "replace_family_type",
        Description =
            "Replaces the type of many elements at once (bulk) — family types and system types " +
            "(walls, floors, roofs, pipes, ducts). Target type: targetTypeId, familyName + " +
            "typeName, or typeName + categoryName/categoryId for a system type. Elements: " +
            "elementIds[] (explicit list, 1..2000) or fromTypeIds[] (every element of those " +
            "types). Elements already on the target type are skipped; an element the target " +
            "type does not fit is reported per-item as type_not_valid_for_element. undo_last " +
            "restores the previous types. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""targetTypeId"":{""type"":""integer"",""description"":""Type id to switch to (list_family_types / list_system_types)""},""familyName"":{""type"":""string"",""description"":""Target family, when targetTypeId is unknown""},""typeName"":{""type"":""string"",""description"":""Target type name""},""categoryName"":{""type"":""string"",""description"":""System types: category of the target type (Walls, OST_Walls...)""},""categoryId"":{""type"":""integer"",""description"":""System types: category id""},""elementIds"":{""type"":""array"",""minItems"":1,""maxItems"":2000,""items"":{""type"":""integer""},""description"":""Explicit element list""},""fromTypeIds"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""integer""},""description"":""Every element of these source types""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var target = AgentFamilyTypeResolver.ResolveElementType(
            doc,
            TryGetLong(args, "targetTypeId"),
            AgentJson.GetString(args, "familyName"),
            AgentJson.GetString(args, "typeName"),
            TryGetLong(args, "categoryId"),
            AgentJson.GetString(args, "categoryName"),
            out var errorCode,
            out var message);
        if (target is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode == "type_not_found" ? "target_type_not_found" : errorCode;
            outcome.Message = message!;
            return;
        }

        var elementIds = AgentJson.GetLongArray(args, "elementIds");
        var fromTypeIds = AgentJson.GetLongArray(args, "fromTypeIds");
        if (elementIds.Count == 0 && fromTypeIds.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass elementIds[] (explicit list) or fromTypeIds[] " +
                              "(every instance of the source types).";
            return;
        }

        if (elementIds.Count > MaxElements)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "elementIds accepts at most " + MaxElements + " ids per call; " +
                              "split the request.";
            return;
        }

        var requested = new List<long>(Math.Max(elementIds.Count, fromTypeIds.Count));
        if (elementIds.Count > 0)
        {
            requested.AddRange(elementIds);
        }
        else
        {
            requested.AddRange(CollectInstancesOfTypes(doc, fromTypeIds, ct));
            if (requested.Count > MaxElements)
            {
                outcome.Success = false;
                outcome.ErrorCode = "limit_exceeded";
                outcome.Message = "The source types have " + requested.Count +
                                  " instances; the limit is " + MaxElements +
                                  " per call. Split by elementIds or use start_job.";
                return;
            }
        }

        outcome.Description = "Replace family type with " + target.Name;
        outcome.Data["targetTypeId"] = target.Id.GetValue();
        outcome.Data["targetTypeName"] = target.Name;

        var valid = new List<(Element Element, ElementId OldTypeId)>();
        foreach (var elementId in requested)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = elementId };
            var element = doc.GetElement(ElementIdCompat.Create(elementId));
            if (element is null)
            {
                report.ErrorCode = "element_not_found";
                report.Reason = "Element " + elementId + " does not exist in the document.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (element is ElementType)
            {
                report.ErrorCode = "type_not_valid_for_element";
                report.Reason = "Element " + elementId + " is a type, not a model element.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var currentTypeId = element.GetTypeId();
            if (currentTypeId == target.Id)
            {
                report.Name = element.Name;
                report.Reason = "The element already uses the target type (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (!element.IsValidType(target.Id))
            {
                report.Name = element.Name;
                report.ErrorCode = "type_not_valid_for_element";
                report.Reason = "The type '" + target.Name + "' does not fit element '" +
                                element.Name + "' (" + element.Category?.Name + ").";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            valid.Add((element, currentTypeId));
        }

        if (valid.Count == 0)
        {
            outcome.AffectedCount = 0;
            return;
        }

        var validIds = new List<ElementId>(valid.Count);
        foreach (var pair in valid)
            validIds.Add(pair.Element.Id);

        if (dryRun)
        {
            foreach (var pair in valid)
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = pair.Element.Id.GetValue(),
                    Name = pair.Element.Name,
                    OldValue = doc.GetElement(pair.OldTypeId)?.Name,
                    NewValue = target.Name,
                    Reason = "Dry run: the element would switch to type '" + target.Name + "'."
                });
            }

            outcome.AffectedCount = valid.Count;
            return;
        }

        try
        {
            Element.ChangeTypeId(doc, validIds, target.Id);
        }
        catch (Exception ex)
        {
            foreach (var pair in valid)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Id = pair.Element.Id.GetValue(),
                    Name = pair.Element.Name,
                    ErrorCode = "type_change_failed",
                    Reason = "Revit refused the type change: " + ex.Message
                });
            }

            outcome.Success = false;
            outcome.ErrorCode = "type_change_failed";
            outcome.Message = "Revit refused to change " + valid.Count + " element(s) to type '" +
                              target.Name + "': " + ex.Message;
            return;
        }

        var onTarget = 0;
        foreach (var collectorElement in new FilteredElementCollector(doc)
                     .WhereElementIsNotElementType())
        {
            if (collectorElement.GetTypeId() == target.Id)
                onTarget++;
        }

        foreach (var pair in valid)
        {
            var oldTypeId = pair.OldTypeId.GetValue();
            var newTypeId = target.Id.GetValue();
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = pair.Element.Id.GetValue(),
                Name = pair.Element.Name,
                OldValue = pair.OldTypeId.GetValue().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                NewValue = newTypeId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Detail = doc.GetElement(pair.OldTypeId)?.Name + " -> " + target.Name
            });
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = pair.Element.UniqueId,
                ElementId = pair.Element.Id.GetValue(),
                ParameterName = "element_type",
                OldValue = oldTypeId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                NewValue = newTypeId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Status = ChangeStatus.Applied
            });
        }

        outcome.AffectedCount = valid.Count;
        outcome.Data["movedCount"] = valid.Count;
        outcome.Data["nowOnTargetCount"] = onTarget;
    }

    /// <summary>
    /// Every model element of the source types. System types belong to walls,
    /// ducts and other non-FamilyInstance elements, so the collector walks
    /// all non-type elements (#441).
    /// </summary>
    private static List<long> CollectInstancesOfTypes(
        Document doc,
        IReadOnlyList<long> fromTypeIds,
        CancellationToken ct)
    {
        var typeSet = new HashSet<long>(fromTypeIds);
        var result = new List<long>();
        foreach (var element in new FilteredElementCollector(doc)
                     .WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();
            if (element is ElementType)
                continue;

            if (typeSet.Contains(element.GetTypeId().GetValue()))
                result.Add(element.Id.GetValue());
        }

        return result;
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
