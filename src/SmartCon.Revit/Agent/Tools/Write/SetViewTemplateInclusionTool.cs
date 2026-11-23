using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Controls which settings a view template takes over (Revit "Include in
/// template"). The API setting lives on the template itself
/// (View.SetNonControlledTemplateParameterIds), so every view that uses the
/// template is affected; the response reports the affected views.
/// </summary>
public sealed partial class SetViewTemplateInclusionTool : AgentWriteToolBase
{
    private sealed class RequestedParameter
    {
        public string Name { get; set; } = string.Empty;

        public bool Included { get; set; }
    }

    private sealed class TemplateParameter
    {
        public long Id { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public string? BuiltInName { get; set; }

        public string? Label { get; set; }
    }

    public SetViewTemplateInclusionTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_view_template_inclusion",
        Description =
            "Changes which settings a view template controls (Revit 'Include in template'). " +
            "Target the template with templateId, or a view with viewId: its template is used. " +
            "parameters[] are detached from the template by default (included=false), so " +
            "set_view_crop/set_view_visibility/set_view_range can write on views that use the " +
            "template; pass included=true to hand the setting back to the template. Aliases: " +
            "viewRange, visibility, scale, detailLevel, discipline, filters, worksets. Crop is " +
            "always controlled by a view template and cannot be detached (Revit limitation). " +
            "Turning 'filters' on replaces the own view filters of every view that uses the " +
            "template (ownFiltersReplaced lists them); view filters belong on the template or on " +
            "views whose template does not control filters. " +
            "The change belongs to the template, so all views using it are affected " +
            "(affectedViews is returned). Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""templateId"":{""type"":""integer""},""viewId"":{""type"":""integer""},""parameters"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Names or aliases: crop, visibility, viewRange, scale, detailLevel, discipline, filters, worksets, or an exact template parameter name""},""included"":{""type"":""boolean"",""default"":false},""items"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{""parameter"":{""type"":""string""},""included"":{""type"":""boolean""}},""required"":[""parameter"",""included""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        var basic = base.PreflightCore(document, args, ct);
        if (!basic.Ok)
            return basic;

        var resolved = Resolve(document, args, out var errorCode, out var message);
        if (resolved is null)
            return PlanPreflightResult.Fail(errorCode ?? "invalid_arguments", message ?? "Invalid arguments.");

        foreach (var request in resolved.Requests)
        {
            ct.ThrowIfCancellationRequested();
            var matches = ResolveParameters(document, resolved.Template, request.Name);
            if (matches.Count == 0)
            {
                var missing = ExplainMissing(resolved.Template, request.Name);
                return PlanPreflightResult.Fail(missing.Code, missing.Message);
            }
        }

        return PlanPreflightResult.Pass();
    }

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var resolved = Resolve(doc, args, out var errorCode, out var message);
        if (resolved is null)
        {
            Fail(outcome, errorCode ?? "invalid_arguments", message ?? "Invalid arguments.");
            return;
        }

        var template = resolved.Template;
        var current = new HashSet<long>(
            template.GetNonControlledTemplateParameterIds().Select(id => id.GetValue()));
        var desired = new HashSet<long>(current);
        var changed = 0;
        var reports = new List<object>();

        foreach (var request in resolved.Requests)
        {
            ct.ThrowIfCancellationRequested();

            var matches = ResolveParameters(doc, template, request.Name);
            if (matches.Count == 0)
            {
                var missing = ExplainMissing(template, request.Name);
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Name = request.Name,
                    ErrorCode = missing.Code,
                    Reason = missing.Message
                });
                outcome.Success = false;
                outcome.ErrorCode ??= missing.Code;
                outcome.Message = missing.Message;
                continue;
            }

            foreach (var match in matches)
            {
                var wasIncluded = !current.Contains(match.Id);
                if (wasIncluded == request.Included)
                {
                    outcome.Batch.Skipped.Add(new WriteItemReport
                    {
                        Name = match.DisplayName,
                        Reason = request.Included
                            ? "The template already controls this setting."
                            : "The setting is already detached from the template."
                    });
                    continue;
                }

                if (request.Included)
                    desired.Remove(match.Id);
                else
                    desired.Add(match.Id);

                changed++;
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = match.Id,
                    Name = match.DisplayName,
                    OldValue = wasIncluded ? "included" : "not included",
                    NewValue = request.Included ? "included" : "not included",
                    Detail = match.BuiltInName ?? match.DisplayName
                });

                reports.Add(new
                {
                    parameter = request.Name,
                    resolvedName = match.DisplayName,
                    builtInName = match.BuiltInName,
                    section = SectionOf(match.BuiltInName),
                    included = request.Included
                });
            }
        }

        if (changed == 0)
        {
            if (outcome.Batch.Failed.Count == 0)
                outcome.Message = "No changes: every requested template setting already matches.";
            AddContext(doc, outcome, template, reports);
            return;
        }

        var oldIds = current.OrderBy(id => id).ToArray();
        var newIds = desired.OrderBy(id => id).ToArray();

        // Turning "V/G Overrides Filters" on hands every view's own filter list to
        // the template. Read the own filters BEFORE the write: afterwards they
        // are gone and nothing in the model remembers them.
        var filtersIncluded = newIds.Contains(FiltersParameterId()) && !current.Contains(FiltersParameterId());
        var losingViews = filtersIncluded
            ? AgentTemplateFilterImpact.ViewsLosingOwnFilters(doc, template)
            : new List<(long ViewId, string ViewName, List<string> Filters)>();
        try
        {
            var all = template.GetTemplateParameterIds()
                .Where(id => desired.Contains(id.GetValue()))
                .ToList();
            template.SetNonControlledTemplateParameterIds(all);
        }
        catch (Exception ex)
        {
            Fail(outcome, "template_inclusion_failed",
                "Revit refused to change the template inclusion: " + ex.Message);
            return;
        }

        var actual = new HashSet<long>(
            template.GetNonControlledTemplateParameterIds().Select(id => id.GetValue()));
        if (!actual.SetEquals(desired))
        {
            // The message used to promise a revert that never happened: the write
            // was already in the model but no change item existed, so undo_last
            // could not see it either. Restore the previous set for real.
            try
            {
                var original = template.GetTemplateParameterIds()
                    .Where(id => Array.IndexOf(oldIds, id.GetValue()) >= 0)
                    .ToList();
                template.SetNonControlledTemplateParameterIds(original);
                var restored = new HashSet<long>(
                    template.GetNonControlledTemplateParameterIds()
                        .Select(id => id.GetValue()));
                Fail(
                    outcome,
                    "read_back_mismatch",
                    restored.SetEquals(oldIds)
                        ? "The template inclusion did not match after the write; the previous " +
                          "state was restored."
                        : "The template inclusion did not match after the write, and restoring " +
                          "the previous state also failed. Re-read the template before retrying.");
            }
            catch (Exception ex)
            {
                Fail(
                    outcome,
                    "read_back_mismatch",
                    "The template inclusion did not match after the write, and restoring the " +
                    "previous state failed: " + ex.Message);
            }

            return;
        }

        outcome.AffectedCount = changed;
        outcome.Description = "Changed template inclusion for '" + template.Name + "'";
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = template.UniqueId,
            ElementId = template.Id.GetValue(),
            ParameterName = "view_template_inclusion",
            OldValue = JsonSerializer.Serialize(oldIds),
            NewValue = JsonSerializer.Serialize(newIds),
            Status = ChangeStatus.Applied
        });

        ReportFilterTakeover(outcome, template, losingViews);

        AddContext(doc, outcome, template, reports);
    }

    private static long FiltersParameterId() =>
        new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS).GetValue();

    private sealed class ResolvedRequest
    {
        public View Template { get; set; } = null!;

        public List<TemplateParameter> Parameters { get; set; } = new();

        public List<RequestedParameter> Requests { get; set; } = new();
    }

    private static ResolvedRequest? Resolve(
        Document doc,
        JsonElement args,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        var templateId = AgentJson.GetLong(args, "templateId", 0);
        var viewId = AgentJson.GetLong(args, "viewId", 0);
        View? template = null;

        if (templateId > 0)
        {
            template = doc.GetElement(ElementIdCompat.Create(templateId)) as View;
            if (template is null || !template.IsTemplate)
            {
                errorCode = "template_not_found";
                message = "Element " + templateId + " is not a view template.";
                return null;
            }
        }
        else if (viewId > 0)
        {
            var view = doc.GetElement(ElementIdCompat.Create(viewId)) as View;
            if (view is null)
            {
                errorCode = "unresolved_id";
                message = "View " + viewId + " does not exist.";
                return null;
            }

            if (view.IsTemplate)
            {
                template = view;
            }
            else
            {
                var assigned = view.ViewTemplateId;
                if (assigned is null || assigned == ElementId.InvalidElementId)
                {
                    errorCode = "no_view_template";
                    message = "View '" + view.Name + "' has no view template. Apply a template " +
                              "first, or pass templateId.";
                    return null;
                }

                template = doc.GetElement(assigned) as View;
            }

            if (template is null || !template.IsTemplate)
            {
                errorCode = "template_not_found";
                message = "The view template of view " + viewId + " does not exist.";
                return null;
            }
        }
        else
        {
            errorCode = "invalid_arguments";
            message = "Pass templateId or viewId.";
            return null;
        }

        var requests = ReadRequests(args, out message);
        if (requests is null || requests.Count == 0)
        {
            errorCode = "invalid_arguments";
            message ??= "Pass at least one parameter name in parameters[] or items[].";
            return null;
        }

        var parameters = new List<TemplateParameter>();
        foreach (var id in template.GetTemplateParameterIds())
        {
            parameters.Add(DescribeParameter(doc, id));
        }

        return new ResolvedRequest
        {
            Template = template,
            Parameters = parameters,
            Requests = requests
        };
    }

    private static List<RequestedParameter>? ReadRequests(JsonElement args, out string? message)
    {
        message = null;
        var result = new List<RequestedParameter>();
        var defaultIncluded = AgentJson.GetBool(args, "included", false);

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("parameters", out var parameters)
            && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in parameters.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(new RequestedParameter { Name = name!, Included = defaultIncluded });
            }
        }

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var name = AgentJson.GetString(item, "parameter");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                result.Add(new RequestedParameter
                {
                    Name = name!,
                    Included = AgentJson.GetBool(item, "included", defaultIncluded)
                });
            }
        }

        if (result.Count == 0)
            message = "Pass at least one parameter name in parameters[] or items[].";
        return result;
    }

    private static TemplateParameter DescribeParameter(Document doc, ElementId id)
    {
        var value = id.GetValue();
        if (value < 0)
        {
            var builtInName = AgentBuiltInParameterNames.GetName(id);
            string? label = null;
            try
            {
                label = LabelUtils.GetLabelFor((BuiltInParameter)value);
            }
            catch (Exception)
            {
            }

            return new TemplateParameter
            {
                Id = value,
                DisplayName = label ?? builtInName,
                BuiltInName = builtInName,
                Label = label
            };
        }

        var name = doc.GetElement(id)?.Name ?? "#" + value;
        return new TemplateParameter
        {
            Id = value,
            DisplayName = name
        };
    }

    private static List<TemplateParameter> ResolveParameters(
        Document doc,
        View template,
        string requested)
    {
        var normalized = NormalizeKey(requested);
        if (normalized.Length == 0)
            return new List<TemplateParameter>();

        var all = template.GetTemplateParameterIds()
            .Select(id => DescribeParameter(doc, id))
            .ToList();

        if (Aliases.TryGetValue(normalized, out var builtInNames))
        {
            var byId = new Dictionary<long, TemplateParameter>();
            foreach (var item in all)
                byId[item.Id] = item;

            var result = new List<TemplateParameter>();
            foreach (var builtInName in builtInNames)
            {
                if (!AgentBuiltInParameterNames.TryGetId(builtInName, out var id))
                    continue;
                if (byId.TryGetValue(id, out var match))
                    result.Add(match);
            }

            return result
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .ToList();
        }

        return all
            .Where(item =>
                string.Equals(NormalizeKey(item.BuiltInName), normalized, StringComparison.Ordinal)
                || string.Equals(NormalizeKey(item.DisplayName), normalized, StringComparison.Ordinal)
                || string.Equals(NormalizeKey(item.Label), normalized, StringComparison.Ordinal))
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
    }

    private static (string Code, string Message) ExplainMissing(
        View template,
        string requested)
    {
        var normalized = NormalizeKey(requested);
        if (Aliases.TryGetValue(normalized, out var builtInNames)
            && builtInNames.Any(name => string.Equals(
                name, "VIEWER_CROP_REGION", StringComparison.Ordinal)))
        {
            return (
                "parameter_not_in_template",
                "Revit does not expose Crop Region in the view-template Include list: a view " +
                "template always controls crop, and a template's crop cannot be edited through " +
                "the API. Remove the template from this view (then set the crop) or accept the " +
                "template's crop.");
        }

        var available = string.Join(
            ", ",
            template.GetTemplateParameterIds()
                .Select(id => DescribeParameter(template.Document, id).DisplayName));
        return (
            "template_parameter_not_found",
            "The template '" + template.Name + "' has no controllable parameter '" + requested +
            "'. Available: " + available + ".");
    }

    private static string? SectionOf(string? builtInName) => builtInName switch
    {
        "VIEWER_CROP_REGION" => "crop",
        "VIS_GRAPHICS_MODEL" or "VIS_GRAPHICS_ANNOTATION" => "visibility",
        "PLAN_VIEW_RANGE" => "viewRange",
        _ => null
    };

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value!.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    private static readonly Dictionary<string, string[]> Aliases =
        new(StringComparer.Ordinal)
        {
            ["CROP"] = new[] { "VIEWER_CROP_REGION" },
            ["VISIBILITY"] = new[] { "VIS_GRAPHICS_MODEL", "VIS_GRAPHICS_ANNOTATION" },
            ["VG"] = new[] { "VIS_GRAPHICS_MODEL", "VIS_GRAPHICS_ANNOTATION" },
            ["VIEWRANGE"] = new[] { "PLAN_VIEW_RANGE" },
            ["SCALE"] = new[]
            {
                "VIEW_SCALE",
                "VIEW_SCALE_PULLDOWN_IMPERIAL",
                "VIEW_SCALE_PULLDOWN_METRIC"
            },
            ["DETAILLEVEL"] = new[] { "VIEW_DETAIL_LEVEL" },
            ["DISCIPLINE"] = new[] { "VIEW_DISCIPLINE" },
            ["FILTERS"] = new[] { "VIS_GRAPHICS_FILTERS" },
            ["WORKSETS"] = new[] { "VIS_GRAPHICS_WORKSETS" },
            ["MODEL"] = new[] { "VIS_GRAPHICS_MODEL" },
            ["ANNOTATION"] = new[] { "VIS_GRAPHICS_ANNOTATION" }
        };

    private static void Fail(WriteOutcome outcome, string errorCode, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
        outcome.Batch.Failed.Add(new WriteItemReport
        {
            ErrorCode = errorCode,
            Reason = message
        });
    }
}