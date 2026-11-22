using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Batch floor-plan creation (real-cases v2 case 1): one call creates a plan
/// view for every level in <c>levels</c>, names it from <c>namePattern</c>
/// ({floor}/{level} placeholders) and ASSIGNS the view template
/// (<c>ViewTemplateId</c>) — the two manual steps the owner had to do by hand
/// (create + select all + apply template). Everything happens in one
/// transaction; undo_last deletes the whole batch.
/// </summary>
public sealed class CreateFloorPlansTool : AgentWriteToolBase
{
    private const int MaxLevels = 200;

    public CreateFloorPlansTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_floor_plans",
        Description =
            "Creates floor plan views for a batch of levels and assigns a view template to " +
            "each one in a single call and a single transaction — both steps the manual " +
            "workflow needs (create + apply template), so the plans are centrally controlled " +
            "by the template. namePattern supports {floor} (floor label: explicit floors map " +
            "by levelId, else the LAST number in the level name, else the level elevation in " +
            "whole metres) and {level} (level name). Guards: level_not_found, " +
            "template_not_found, template_view_type_mismatch (template family must be " +
            "FloorPlan), view_family_type_missing, view_name_taken (against the whole " +
            "document AND duplicates inside the batch). undo_last deletes the whole batch. " +
            "Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""levels"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""integer""},""description"":""Level ids (list_levels); one plan per level""},""templateId"":{""type"":""integer"",""description"":""View template id (list_view_templates); must belong to the FloorPlan family""},""namePattern"":{""type"":""string"",""description"":""Name pattern with {floor} and {level} placeholders, e.g. \""План_{floor}\""""},""viewFamilyTypeId"":{""type"":""integer"",""description"":""Optional FloorPlan ViewFamilyType id (default: first matching)""},""floors"":{""type"":""object"",""description"":""Explicit floor labels by level id (keys are level ids as strings), e.g. floors levelId 123 -> label 1, 124 -> 2; wins over auto-parsing of the level name"",""additionalProperties"":{""type"":""string""}},""skipExisting"":{""type"":""boolean"",""default"":true,""description"":""A view with the planned name already existing (e.g. the reference plan) is skipped untouched instead of failing the item""},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""levels"",""templateId"",""namePattern""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var levels = AgentJson.GetLongArray(args, "levels");
        var namePattern = AgentJson.GetString(args, "namePattern");
        var templateId = ReadLong(args, "templateId");
        var floors = ReadFloors(args);

        if (levels.Count == 0)
        {
            Fail(outcome, "invalid_arguments", "Pass levels (list_levels): 1.." + MaxLevels +
                                              " level ids, one plan per level.");
            return;
        }

        if (levels.Count > MaxLevels)
        {
            Fail(outcome, "limit_exceeded",
                "At most " + MaxLevels + " levels per call are allowed; split the batch.");
            return;
        }

        if (string.IsNullOrWhiteSpace(namePattern))
        {
            Fail(outcome, "invalid_arguments", "namePattern is required, e.g. \"План_{floor}\".");
            return;
        }

        if (templateId is null || templateId <= 0)
        {
            Fail(outcome, "invalid_arguments", "templateId is required (list_view_templates).");
            return;
        }

        var template = ResolveTemplate(doc, templateId.Value, out var templateCode, out var templateReason);
        if (template is null)
        {
            Fail(outcome, templateCode, templateReason);
            return;
        }

        var familyType = ResolveFamilyType(doc, args, out var familyCode, out var familyReason);
        if (familyType is null)
        {
            Fail(outcome, familyCode, familyReason);
            return;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        // Existing view with the planned name is a no-op skip by default (the
        // reference plan of the pattern stays untouched), see the card.
        var skipExisting = AgentJson.GetBool(args, "skipExisting", true);
        outcome.Description = "Created " + levels.Count + " floor plan(s) from '" + namePattern + "'";

        // Names are unique across the document for views (conservative check,
        // same as create_view); the set grows with every view created in this
        // batch so in-batch duplicates are caught too.
        var viewsByName = AgentViewWriteHelper.CollectViewNamesByName(doc);
        var usedNames = new HashSet<string>(viewsByName.Keys, StringComparer.OrdinalIgnoreCase);
        var created = new List<object>();
        var floorLabels = new Dictionary<long, string>();
        foreach (var pair in floors)
            floorLabels[pair.Key] = pair.Value;

        foreach (var levelId in levels)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = levelId };
            if (!AgentViewWriteHelper.TryGetLevel(doc, levelId, out var level, out var levelReason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "level_not_found", levelReason);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            report.Name = level.Name;
            var floorLabel = ResolveFloorLabel(level, levelId, floorLabels, outcome);
            var name = BuildName(namePattern!, floorLabel, level.Name);
            var nameError = AgentViewWriteHelper.ValidateName(name);
            if (nameError is not null)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "invalid_name", nameError);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (usedNames.Contains(name!))
            {
                viewsByName.TryGetValue(name!, out var holderId);
                var holderText = holderId > 0
                    ? "view id " + holderId
                    : "a view/sheet in this batch";
                if (skipExisting && holderId > 0)
                {
                    report.Reason = "Name '" + name + "' already exists (" + holderText +
                                    "); left untouched, only the missing plans were created.";
                    outcome.Batch.Skipped.Add(report);
                    continue;
                }

                ApplyViewTemplateTool.Fail(
                    outcome, report, "view_name_taken",
                    "The name '" + name + "' is already used by " + holderText +
                    " (or in this batch). Change namePattern or the floors labels, or pass " +
                    "skipExisting=true to keep the existing view (e.g. the reference plan) " +
                    "and create only the missing ones.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var view = AgentViewCreateHelper.CreateNamedView(
                doc, familyType, level, name!, templateId, scale: null,
                outcome.Warnings, out var code, out var reason);
            if (view is null)
            {
                report.Name = name;
                ApplyViewTemplateTool.Fail(outcome, report, code!, reason!);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            usedNames.Add(name!);
            var genLevelId = (view as ViewPlan)?.GenLevel?.Id.GetValue();

            if (dryRun)
            {
                report.Name = view.Name;
                report.NewValue = name;
                report.Reason = "Dry run: the floor plan would be created as '" + name +
                                "' with template '" + template.Name + "'.";
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
                created.Add(new
                {
                    viewId = (long?)null,
                    levelId,
                    name,
                    templateId = templateId.Value,
                    genLevelId
                });
                continue;
            }

            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = view.Id.GetValue(),
                Name = view.Name,
                NewValue = view.Name,
                Detail = "floor plan, template '" + template.Name + "'"
            });
            outcome.AffectedCount++;
            outcome.Items.Add(new ChangeItem
            {
                UniqueId = view.UniqueId,
                ElementId = view.Id.GetValue(),
                ParameterName = "view_created",
                NewValue = view.Name,
                Status = ChangeStatus.Applied
            });
            created.Add(new
            {
                viewId = view.Id.GetValue(),
                levelId,
                name = view.Name,
                templateId = view.ViewTemplateId.GetValue(),
                genLevelId
            });
        }

        outcome.Data["created"] = created;
        outcome.Data["createdCount"] = created.Count;
    }

    private static View? ResolveTemplate(
        Document doc,
        long templateId,
        out string errorCode,
        out string reason)
    {
        errorCode = string.Empty;
        reason = string.Empty;

        if (doc.GetElement(ElementIdCompat.Create(templateId)) is not View template
            || !template.IsTemplate)
        {
            errorCode = "template_not_found";
            reason = "Element " + templateId + " is not a view template (list_view_templates).";
            return null;
        }

        // Direct ViewType check: the ViewFamilyType of a template is not always
        // resolvable via GetTypeId (a 3D template created by CreateViewTemplate
        // resolved to null and slipped through a family-based guard — IT probe).
        if (template.ViewType != ViewType.FloorPlan)
        {
            errorCode = "template_view_type_mismatch";
            reason = "Template '" + template.Name + "' is " + template.ViewType +
                     " and cannot be assigned to floor plans; pass a FloorPlan template.";
            return null;
        }

        return template;
    }

    private static ViewFamilyType? ResolveFamilyType(
        Document doc,
        JsonElement args,
        out string errorCode,
        out string reason)
    {
        errorCode = string.Empty;
        reason = string.Empty;

        var requestedId = ReadLong(args, "viewFamilyTypeId");
        if (requestedId.HasValue)
        {
            if (doc.GetElement(ElementIdCompat.Create(requestedId.Value)) is ViewFamilyType requested
                && requested.ViewFamily == ViewFamily.FloorPlan)
            {
                return requested;
            }

            errorCode = "view_family_type_missing";
            reason = "Element " + requestedId.Value +
                     " is not a ViewFamilyType of family FloorPlan.";
            return null;
        }

        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)))
        {
            if (element is ViewFamilyType candidate && candidate.ViewFamily == ViewFamily.FloorPlan)
                return candidate;
        }

        errorCode = "view_family_type_missing";
        reason = "The document has no ViewFamilyType of family FloorPlan. " +
                 "Load a family that provides it.";
        return null;
    }

    /// <summary>
    /// Floor label precedence: explicit floors[levelId] → the LAST number group
    /// in the level name («Этаж 3» → 3) → the level elevation in whole metres.
    /// </summary>
    private static string ResolveFloorLabel(
        Level level,
        long levelId,
        Dictionary<long, string> floorLabels,
        WriteOutcome outcome)
    {
        if (floorLabels.TryGetValue(levelId, out var explicitLabel)
            && !string.IsNullOrWhiteSpace(explicitLabel))
        {
            return explicitLabel.Trim();
        }

        var fromName = LastNumberInName(level.Name);
        if (fromName is not null)
            return fromName;

        var metres = Math.Round(
            UnitUtils.ConvertFromInternalUnits(level.Elevation, UnitTypeId.Meters),
            MidpointRounding.AwayFromZero);
        var label = ((long)metres).ToString(System.Globalization.CultureInfo.InvariantCulture);
        outcome.Warnings.Add(
            "Level '" + level.Name + "' has no number in its name; the {floor} placeholder " +
            "falls back to the elevation in whole metres (" + label +
            "). [Action: pass floors={\"" + levelId + "\":\"<label>\"} to control the name explicitly]");
        return label;
    }

    private static string? LastNumberInName(string name)
    {
        var lastStart = -1;
        var lastLength = 0;
        var index = 0;
        while (index < name.Length)
        {
            if (!char.IsDigit(name[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < name.Length && char.IsDigit(name[index]))
                index++;

            lastStart = start;
            lastLength = index - start;
        }

        return lastStart < 0 ? null : name.Substring(lastStart, lastLength);
    }

    private static string BuildName(string pattern, string floor, string levelName) =>
        pattern.Replace("{floor}", floor).Replace("{level}", levelName);

    private static List<KeyValuePair<long, string>> ReadFloors(JsonElement args)
    {
        var result = new List<KeyValuePair<long, string>>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("floors", out var floors)
            || floors.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in floors.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && long.TryParse(property.Name, out var levelId))
            {
                result.Add(new KeyValuePair<long, string>(levelId, property.Value.GetString()!));
            }
        }

        return result;
    }

    private static void Fail(WriteOutcome outcome, string errorCode, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
    }

    private static long? ReadLong(JsonElement args, string property)
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

/// <summary>
/// Shared create-a-view mechanics for the batch view tools (create_floor_plans,
/// create_views_batch): create → scale → rename → assign template, in the
/// caller's transaction. Rename and template failures restore nothing but
/// report a per-item error so atomic batches roll back cleanly.
/// </summary>
internal static class AgentViewCreateHelper
{
    internal static View? CreateNamedView(
        Document doc,
        ViewFamilyType familyType,
        Level? level,
        string name,
        long? templateId,
        int? scale,
        List<string> warnings,
        out string? errorCode,
        out string? reason)
    {
        errorCode = null;
        reason = null;

        View view;
        try
        {
            view = level is null
                ? View3D.CreateIsometric(doc, familyType.Id)
                : ViewPlan.Create(doc, familyType.Id, level.Id);
        }
        catch (Exception ex)
        {
            errorCode = "view_creation_failed";
            reason = "Revit refused to create the view: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }

        try
        {
            view.Name = name;
        }
        catch (Exception ex)
        {
            errorCode = "view_name_taken";
            reason = "Revit refused the name '" + name + "': " + ex.Message;
            return null;
        }

        // Scale goes AFTER the template: assigning a template re-applies every
        // parameter it controls (VIEW_SCALE included), so a scale written first
        // would be overwritten. When the template controls VIEW_SCALE, Revit
        // silently ignores the write — verified and reported as a warning.
        if (templateId is not null && templateId > 0)
        {
            // overwrite=true: a freshly created view may already carry the
            // DefaultTemplateId of its ViewFamilyType; the explicit template wins.
            var change = AgentViewSettingsWriter.ApplyTemplate(doc, view, templateId.Value, overwrite: true);
            if (change.ErrorCode is not null)
            {
                errorCode = change.ErrorCode;
                reason = change.Reason;
                return null;
            }
        }

        if (scale is > 0)
        {
            try
            {
                view.Scale = scale.Value;
                if (view.Scale != scale.Value)
                {
                    warnings.Add(
                        "The scale of '" + name + "' stays " + view.Scale +
                        " (requested " + scale.Value + "): the view template controls VIEW_SCALE. " +
                        "[Action: change the scale on the template or detach it with " +
                        "set_view_template_inclusion(parameters=[\"scale\"], included=false)]");
                }
            }
            catch (Exception ex)
            {
                warnings.Add(
                    "The scale of '" + name + "' could not be set to " + scale.Value + ": " +
                    ex.Message + " [Action: set the scale on the view template or with " +
                    "set_view_parameters]");
            }
        }

        return view;
    }
}
