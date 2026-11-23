using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Mixed batch view creation (real-cases v2 case 4 and the generic case): one
/// call creates up to 100 views of kind plan (ViewPlan.Create, levelId
/// required) or 3d (View3D.CreateIsometric), names each, optionally assigns a
/// view template (ViewTemplateId) and scale — in a single transaction.
/// undo_last deletes the whole batch.
/// </summary>
public sealed class CreateViewsBatchTool : AgentWriteToolBase
{
    private const int MaxItems = 100;

    public CreateViewsBatchTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_views_batch",
        Description =
            "Creates several views in one call and one transaction: items with kind=plan " +
            "(ViewPlan.Create, levelId required) or kind=3d (isometric). Every item gets its " +
            "own name, optional templateId (assigned as ViewTemplateId — real assignment, not " +
            "a settings copy), optional viewFamilyTypeId, scale and detailLevel (applied after the " +
            "template; a 3D view at Coarse/Medium warns that MEP curves are single lines). " +
            "Guards: level_not_found, " +
            "template_not_found, template_view_type_mismatch (plan items need a FloorPlan " +
            "family template, 3d items a ThreeDimensional one), view_family_type_missing, " +
            "view_name_taken (against the whole document AND duplicates inside the batch; " +
            "skipExisting=true leaves an existing view of that name untouched instead). A " +
            "template that includes V/G Overrides Filters owns the filter list of the new " +
            "views: create_view_filter will refuse them (view_controlled_by_template) — the " +
            "result warns and marks templateControlsFilters per view. " +
            "undo_last deletes the whole batch. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""plan"",""3d""],""description"":""Kind of the view to create""},""name"":{""type"":""string"",""description"":""Name of the new view""},""levelId"":{""type"":""integer"",""description"":""plan: level (list_levels), required for kind=plan""},""templateId"":{""type"":""integer"",""description"":""Optional view template id assigned to the created view""},""viewFamilyTypeId"":{""type"":""integer"",""description"":""Optional ViewFamilyType id matching the kind""},""scale"":{""type"":""integer"",""description"":""Optional view scale (e.g. 100)""},""detailLevel"":{""type"":""string"",""enum"":[""coarse"",""medium"",""fine""],""description"":""Optional detail level; Fine is required to show MEP fittings instead of single lines""}},""required"":[""kind"",""name""],""additionalProperties"":false}},""skipExisting"":{""type"":""boolean"",""default"":false,""description"":""A view with the item name already existing in the document is skipped untouched (reported in skipped[]) instead of failing with view_name_taken; duplicates inside the batch still fail""},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<CreateViewsBatchRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            Fail(outcome, "invalid_arguments", "Pass items: 1.." + MaxItems +
                                              " view descriptions ({kind, name, ...}).");
            return;
        }

        if (items.Count > MaxItems)
        {
            Fail(outcome, "limit_exceeded",
                "At most " + MaxItems + " items per call are allowed; split the list.");
            return;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        // An existing view with the item name is a failed item by default; with
        // skipExisting=true it is a no-op skip (same contract as create_floor_plans).
        var skipExisting = AgentJson.GetBool(args, "skipExisting", false);
        outcome.Data["skipExisting"] = skipExisting;
        outcome.Description = "Created " + items.Count + " view(s)";

        var viewsByName = AgentViewWriteHelper.CollectViewNamesByName(doc);
        var usedNames = new HashSet<string>(viewsByName.Keys, StringComparer.OrdinalIgnoreCase);
        var created = new List<object>();
        var filterControllingTemplates = new Dictionary<long, string>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Name = item.Name };
            var kind = (item.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (kind is not ("plan" or "3d"))
            {
                ApplyViewTemplateTool.Fail(
                    outcome, report, "invalid_arguments",
                    "Item '" + item.Name + "': kind must be one of: plan, 3d.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            var nameError = AgentViewWriteHelper.ValidateName(item.Name);
            if (nameError is not null)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "invalid_name", nameError);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.DetailLevel)
                && !AgentViewDetailHelper.TryParse(item.DetailLevel, out _))
            {
                ApplyViewTemplateTool.Fail(
                    outcome, report, "invalid_arguments",
                    "Item '" + item.Name + "': detailLevel must be one of: coarse, medium, fine.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (usedNames.Contains(item.Name!))
            {
                viewsByName.TryGetValue(item.Name!, out var holderId);
                var holderText = holderId > 0
                    ? "view id " + holderId
                    : "a view/sheet created earlier in this batch";
                if (skipExisting && holderId > 0)
                {
                    report.Id = holderId;
                    report.Reason = "Name '" + item.Name + "' already exists (" + holderText +
                                    "); left untouched, only the missing views were created.";
                    outcome.Batch.Skipped.Add(report);
                    continue;
                }

                ApplyViewTemplateTool.Fail(
                    outcome, report, "view_name_taken",
                    "The name '" + item.Name + "' is already used by " + holderText +
                    " (or in this batch). Change the name, or pass skipExisting=true to keep " +
                    "the existing view and create only the missing ones.");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            Level? level = null;
            if (kind == "plan")
            {
                if (item.LevelId is null)
                {
                    ApplyViewTemplateTool.Fail(
                        outcome, report, "invalid_arguments",
                        "Item '" + item.Name + "': kind=plan requires levelId (list_levels).");
                    if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                    continue;
                }

                if (!AgentViewWriteHelper.TryGetLevel(doc, item.LevelId.Value, out level, out var levelReason))
                {
                    ApplyViewTemplateTool.Fail(outcome, report, "level_not_found", levelReason);
                    if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                    continue;
                }
            }

            var family = kind == "plan" ? ViewFamily.FloorPlan : ViewFamily.ThreeDimensional;
            var familyType = ResolveFamilyType(doc, item.ViewFamilyTypeId, family);
            if (familyType is null)
            {
                ApplyViewTemplateTool.Fail(
                    outcome, report, "view_family_type_missing",
                    "Item '" + item.Name + "': no ViewFamilyType of family " + family +
                    (item.ViewFamilyTypeId.HasValue
                        ? " (element " + item.ViewFamilyTypeId.Value + " does not match)"
                        : " in the document") + ".");
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            if (item.TemplateId is not null)
            {
                var templateError = TemplateGuardError(
                    doc, item.TemplateId.Value, family, out var templateReason);
                if (templateError is not null)
                {
                    ApplyViewTemplateTool.Fail(outcome, report, templateError, templateReason);
                    if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                    continue;
                }

                if (doc.GetElement(ElementIdCompat.Create(item.TemplateId.Value)) is View template
                    && AgentTemplateFilterImpact.ControlsFilters(template))
                {
                    filterControllingTemplates[template.Id.GetValue()] = template.Name;
                }
            }

            var view = AgentViewCreateHelper.CreateNamedView(
                doc, familyType, level, item.Name!, item.TemplateId, item.Scale,
                outcome.Warnings, out var code, out var reason);
            if (view is null)
            {
                ApplyViewTemplateTool.Fail(outcome, report, code!, reason!);
                if (ApplyViewTemplateTool.Abort(outcome, atomic)) return;
                continue;
            }

            usedNames.Add(item.Name!);
            if (kind != "legend")
            {
                AgentViewDetailHelper.Apply(view, item.DetailLevel, outcome.Warnings);
                if (kind == "3d")
                    AgentViewDetailHelper.WarnIfMepLooksSchematic(view, outcome.Warnings);
            }

            var genLevelId = (view as ViewPlan)?.GenLevel?.Id.GetValue();
            var appliedTemplateId = view.ViewTemplateId == ElementId.InvalidElementId
                ? (long?)null
                : view.ViewTemplateId.GetValue();
            var templateControlsFilters = appliedTemplateId.HasValue
                                          && filterControllingTemplates.ContainsKey(appliedTemplateId.Value);

            if (dryRun)
            {
                report.Id = null;
                report.NewValue = item.Name;
                report.Reason = "Dry run: the " + kind + " view would be created as '" +
                                item.Name + "'" +
                                (appliedTemplateId.HasValue
                                    ? " with template " + appliedTemplateId.Value
                                    : string.Empty) + ".";
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
                created.Add(new
                {
                    viewId = (long?)null,
                    kind,
                    name = item.Name,
                    levelId = item.LevelId,
                    templateId = appliedTemplateId,
                    templateControlsFilters,
                    genLevelId
                });
                continue;
            }

            report.Id = view.Id.GetValue();
            report.Name = view.Name;
            report.NewValue = view.Name;
            report.Detail = kind + (appliedTemplateId.HasValue
                ? ", template " + appliedTemplateId.Value
                : string.Empty);
            outcome.Batch.Changed.Add(report);
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
                kind,
                name = view.Name,
                levelId = item.LevelId,
                templateId = appliedTemplateId,
                templateControlsFilters,
                genLevelId
            });
        }

        foreach (var pair in filterControllingTemplates.OrderBy(pair => pair.Key))
        {
            outcome.Warnings.Add(
                "Template '" + pair.Value + "' includes 'V/G Overrides Filters', so it owns the " +
                "filter list of the new views: create_view_filter and set_filter_overrides will " +
                "refuse them (view_controlled_by_template). Add the view filters to the template " +
                "itself (viewIds=[" + pair.Key + "]) or detach 'filters' from the template first " +
                "(set_view_template_inclusion(parameters=[\"filters\"], included=false)).");
        }

        outcome.Data["created"] = created;
        outcome.Data["createdCount"] = created.Count;
    }

    /// <summary>
    /// Pre-create template guard per item: template_not_found for a missing or
    /// non-template id, template_view_type_mismatch when the template view type
    /// does not match the item kind (direct ViewType check — the ViewFamilyType
    /// of a template is not always resolvable via GetTypeId). Null when the
    /// template is applicable.
    /// </summary>
    private static string? TemplateGuardError(
        Document doc,
        long templateId,
        ViewFamily family,
        out string reason)
    {
        if (doc.GetElement(ElementIdCompat.Create(templateId)) is not View template
            || !template.IsTemplate)
        {
            reason = "Item template " + templateId +
                     " is not a view template (list_view_templates).";
            return "template_not_found";
        }

        var expectedType = family == ViewFamily.FloorPlan ? ViewType.FloorPlan : ViewType.ThreeD;
        if (template.ViewType != expectedType)
        {
            reason = "Template '" + template.Name + "' is " + template.ViewType +
                     " and cannot be assigned to a " + expectedType + " view.";
            return "template_view_type_mismatch";
        }

        reason = string.Empty;
        return null;
    }

    private static ViewFamilyType? ResolveFamilyType(
        Document doc,
        long? requestedId,
        ViewFamily family)
    {
        if (requestedId.HasValue
            && doc.GetElement(ElementIdCompat.Create(requestedId.Value)) is ViewFamilyType requested
            && requested.ViewFamily == family)
        {
            return requested;
        }

        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)))
        {
            if (element is ViewFamilyType candidate && candidate.ViewFamily == family)
                return candidate;
        }

        return null;
    }

    private static void Fail(WriteOutcome outcome, string errorCode, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
    }

    private sealed class CreateViewsBatchRequest
    {
        public List<CreateViewsBatchItem>? Items { get; set; }
    }

    private sealed class CreateViewsBatchItem
    {
        public string? Kind { get; set; }

        public string? Name { get; set; }

        public long? LevelId { get; set; }

        public long? TemplateId { get; set; }

        public long? ViewFamilyTypeId { get; set; }

        public int? Scale { get; set; }

        public string? DetailLevel { get; set; }
    }
}
