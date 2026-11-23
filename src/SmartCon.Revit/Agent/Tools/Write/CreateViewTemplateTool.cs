using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Creates a view template from an existing view and optionally assigns it.
/// The reason it exists: a project template that includes "V/G Overrides
/// Filters" replaces the per-view filter list, and the API cannot detach a
/// filter from a view while a template owns it. With detachParameters=["filters"]
/// the new template leaves filters to each view, so one shared 3D template and
/// a per-system filter can coexist (owner feedback 2026-11-24).
/// </summary>
public sealed class CreateViewTemplateTool : AgentWriteToolBase
{
    public CreateViewTemplateTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_view_template",
        Description =
            "Creates a view template from an existing view (View.CreateViewTemplate) and " +
            "optionally assigns it to viewIds. The source view keeps its settings; the new " +
            "template is renamed to name. detachParameters (crop, viewRange, visibility, scale, " +
            "detailLevel, discipline, filters, worksets, or exact BuiltInParameter names) makes " +
            "the template NOT control those settings: detachParameters=[\"filters\"] gives a " +
            "shared 3D template under which every view keeps its own filter — the standard " +
            "project convention the API could not express before. A template that keeps " +
            "filters warns about the own filters of assigned views it replaces " +
            "(ownFiltersReplaced). Guards: view_not_found, source_is_template, " +
            "view_not_valid_for_template, view_name_taken, template_not_applicable. " +
            "undo_last deletes the created template. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sourceViewId"":{""type"":""integer"",""description"":""A regular view (not a template) whose settings the template copies""},""name"":{""type"":""string"",""description"":""Name of the new view template""},""viewIds"":{""type"":""array"",""items"":{""type"":""integer""},""description"":""Optional views to assign the new template to""},""detachParameters"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Settings the template must NOT control: crop, viewRange, visibility, scale, detailLevel, discipline, filters, worksets, or exact BuiltInParameter names""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""sourceViewId"",""name""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var sourceViewId = AgentJson.GetLong(args, "sourceViewId", 0);
        var name = AgentJson.GetString(args, "name")?.Trim();
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        var detach = AgentJson.GetStringArray(args, "detachParameters");

        if (sourceViewId <= 0)
        {
            Fail(outcome, "invalid_arguments", "sourceViewId is required.");
            return;
        }

        var nameError = AgentViewWriteHelper.ValidateName(name);
        if (nameError is not null)
        {
            Fail(outcome, "invalid_name", nameError);
            return;
        }

        if (doc.GetElement(ElementIdCompat.Create(sourceViewId)) is not View source)
        {
            Fail(outcome, "view_not_found", "No view with id " + sourceViewId + " exists.");
            return;
        }

        if (source.IsTemplate)
        {
            Fail(
                outcome, "source_is_template",
                "View " + sourceViewId + " is already a template. Pass a regular view with the " +
                "desired settings; the API creates templates from views, not from templates.");
            return;
        }

        if (!source.IsViewValidForTemplateCreation())
        {
            Fail(
                outcome, "view_not_valid_for_template",
                "Revit does not allow a template to be created from " + source.ViewType +
                " view '" + source.Name + "'.");
            return;
        }

        if (AgentViewWriteHelper.CollectViewNamesByName(doc).ContainsKey(name!))
        {
            Fail(
                outcome, "view_name_taken",
                "The name '" + name + "' is already used by a view/sheet. Pass a different name.");
            return;
        }

        if (!TryMapDetachParameters(detach, out var detachBuiltIns, out var detachError))
        {
            Fail(outcome, "invalid_arguments", detachError!);
            return;
        }

        outcome.Description = "Create view template '" + name + "' from '" + source.Name + "'";

        View template;
        try
        {
            template = source.CreateViewTemplate();
        }
        catch (Exception ex)
        {
            Fail(
                outcome, "template_creation_failed",
                "Revit refused to create a template from '" + source.Name + "': " +
                ex.GetType().Name + ": " + ex.Message);
            return;
        }

        try
        {
            template.Name = name!;
        }
        catch (Exception ex)
        {
            TryDelete(doc, template);
            Fail(
                outcome, "view_name_taken",
                "Revit refused the template name '" + name + "': " + ex.Message);
            return;
        }

        var detached = ApplyDetachedParameters(template, detachBuiltIns, outcome.Warnings);
        var assigned = dryRun ? PlanAssignments(doc, template, viewIds, source, outcome) : Assign(
            doc, template, viewIds, source, outcome, ct);
        if (outcome.Success == false)
            return;

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = template.Name,
                NewValue = name,
                Detail = Describe(source, detached, assigned),
                Reason = "Dry run: a view template '" + name + "' would be created from '" +
                         source.Name + "'."
            });
            outcome.AffectedCount = 1;
            return;
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = template.Id.GetValue(),
            Name = template.Name,
            NewValue = template.Name,
            Detail = Describe(source, detached, assigned)
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = template.UniqueId,
            ElementId = template.Id.GetValue(),
            ParameterName = "view_created",
            NewValue = template.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["templateId"] = template.Id.GetValue();
        outcome.Data["name"] = template.Name;
        outcome.Data["sourceViewId"] = sourceViewId;
        outcome.Data["detachedParameters"] = detached;
        outcome.Data["assignedViewIds"] = assigned;
    }

    private static List<long> PlanAssignments(
        Document doc,
        View template,
        IReadOnlyList<long> viewIds,
        View source,
        WriteOutcome outcome)
    {
        var planned = new List<long>();
        foreach (var viewId in viewIds)
        {
            if (doc.GetElement(ElementIdCompat.Create(viewId)) is not View view
                || view.IsTemplate
                || !view.IsValidViewTemplate(template.Id))
            {
                outcome.Warnings.Add(
                    "View " + viewId + " is not a valid target for template '" + template.Name +
                    "' and was skipped.");
                continue;
            }

            WarnLostFilters(doc, view, template, outcome);
            planned.Add(viewId);
        }

        return planned;
    }

    private static List<long> Assign(
        Document doc,
        View template,
        IReadOnlyList<long> viewIds,
        View source,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        var assigned = new List<long>();
        foreach (var viewId in viewIds)
        {
            ct.ThrowIfCancellationRequested();
            if (doc.GetElement(ElementIdCompat.Create(viewId)) is not View view || view.IsTemplate)
            {
                outcome.Warnings.Add("View " + viewId + " is missing or is a template; skipped.");
                continue;
            }

            if (!view.IsValidViewTemplate(template.Id))
            {
                outcome.Warnings.Add(
                    "View '" + view.Name + "' (" + viewId + ") cannot use template '" +
                    template.Name + "': the view types differ; skipped.");
                continue;
            }

            WarnLostFilters(doc, view, template, outcome);
            try
            {
                view.ViewTemplateId = template.Id;
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "Template '" + template.Name + "' could not be assigned to view '" +
                    view.Name + "': " + ex.Message);
                continue;
            }

            if (view.ViewTemplateId.GetValue() == template.Id.GetValue())
                assigned.Add(viewId);
            else
                outcome.Warnings.Add(
                    "Revit did not keep template '" + template.Name + "' on view '" +
                    view.Name + "'; the view keeps its previous template.");
        }

        return assigned;
    }

    private static void WarnLostFilters(
        Document doc,
        View view,
        View template,
        WriteOutcome outcome)
    {
        if (!AgentTemplateFilterImpact.ControlsFilters(template))
            return;

        var lost = AgentTemplateFilterImpact.FiltersLostOnAssignment(doc, view, template);
        if (lost.Count == 0)
            return;

        outcome.Warnings.Add(
            "View '" + view.Name + "' loses its own filters " +
            AgentViewSettingsWriter.Describe(lost) +
            ": template '" + template.Name + "' controls filters. " +
            "Create the template with detachParameters=[\"filters\"] to keep per-view filters.");
    }

    private static List<string> ApplyDetachedParameters(
        View template,
        IReadOnlyList<BuiltInParameter> requested,
        List<string> warnings)
    {
        var detached = new List<string>();
        if (requested.Count == 0)
            return detached;

        var available = new HashSet<ElementId>(template.GetTemplateParameterIds());
        var nonControlled = new HashSet<ElementId>(template.GetNonControlledTemplateParameterIds());
        foreach (var parameter in requested)
        {
            var id = ElementIdCompat.Create((long)(int)parameter);
            if (!available.Contains(id))
            {
                warnings.Add(
                    "Parameter " + parameter + " is not part of this template's controllable " +
                    "set and was skipped.");
                continue;
            }

            nonControlled.Add(id);
            detached.Add(parameter.ToString());
        }

        if (detached.Count > 0)
            template.SetNonControlledTemplateParameterIds(nonControlled.ToList());

        return detached;
    }

    private static bool TryMapDetachParameters(
        IReadOnlyList<string> requested,
        out List<BuiltInParameter> builtIns,
        out string? error)
    {
        builtIns = new List<BuiltInParameter>();
        error = null;
        foreach (var raw in requested)
        {
            var key = Normalize(raw);
            if (key.Length == 0)
                continue;

            if (Aliases.TryGetValue(key, out var mapped))
            {
                builtIns.AddRange(mapped);
                continue;
            }

            if (Enum.TryParse<BuiltInParameter>(raw, ignoreCase: true, out var exact))
            {
                builtIns.Add(exact);
                continue;
            }

            error = "Unknown detachParameters value '" + raw + "'. Allowed: crop, viewRange, " +
                    "visibility, scale, detailLevel, discipline, filters, worksets, or an exact " +
                    "BuiltInParameter name.";
            return false;
        }

        return true;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new System.Text.StringBuilder(value!.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    private static string Describe(View source, IReadOnlyList<string> detached, IReadOnlyList<long> assigned)
    {
        var parts = new List<string> { "from '" + source.Name + "'" };
        if (detached.Count > 0)
            parts.Add("detached " + string.Join(", ", detached));
        if (assigned.Count > 0)
            parts.Add("assigned to " + string.Join(", ", assigned));
        return string.Join("; ", parts);
    }

    private static void TryDelete(Document doc, View view)
    {
        try
        {
            doc.Delete(view.Id);
        }
        catch (Exception)
        {
            // Best effort: the caller already has the original failure.
        }
    }

    private static void Fail(WriteOutcome outcome, string errorCode, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
    }

    private static readonly Dictionary<string, BuiltInParameter[]> Aliases =
        new(StringComparer.Ordinal)
        {
            ["CROP"] = new[] { BuiltInParameter.VIEWER_CROP_REGION },
            ["VISIBILITY"] = new[]
            {
                BuiltInParameter.VIS_GRAPHICS_MODEL,
                BuiltInParameter.VIS_GRAPHICS_ANNOTATION
            },
            ["VG"] = new[]
            {
                BuiltInParameter.VIS_GRAPHICS_MODEL,
                BuiltInParameter.VIS_GRAPHICS_ANNOTATION
            },
            ["VIEWRANGE"] = new[] { BuiltInParameter.PLAN_VIEW_RANGE },
            ["SCALE"] = new[]
            {
                BuiltInParameter.VIEW_SCALE,
                BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL,
                BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC
            },
            ["DETAILLEVEL"] = new[] { BuiltInParameter.VIEW_DETAIL_LEVEL },
            ["DISCIPLINE"] = new[] { BuiltInParameter.VIEW_DISCIPLINE },
            ["FILTERS"] = new[] { BuiltInParameter.VIS_GRAPHICS_FILTERS },
            ["WORKSETS"] = new[] { BuiltInParameter.VIS_GRAPHICS_WORKSETS },
            ["MODEL"] = new[] { BuiltInParameter.VIS_GRAPHICS_MODEL },
            ["ANNOTATION"] = new[] { BuiltInParameter.VIS_GRAPHICS_ANNOTATION }
        };
}