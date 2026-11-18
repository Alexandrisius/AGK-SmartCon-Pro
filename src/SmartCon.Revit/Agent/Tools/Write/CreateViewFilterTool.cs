using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class CreateViewFilterTool : AgentWriteToolBase
{
    private const int MaxViews = 50;
    private const int MaxCategories = 10;

    public CreateViewFilterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "create_view_filter",
        Description =
            "Creates (or reuses by name) a parameter view filter and applies it to views, " +
            "optionally hiding or showing the matching elements. Conditions use a generic DSL: " +
            "{field: category|family|type|level|room|system|parameter, parameter: \"Система\", " +
            "op: eq|neq|contains|notcontains|beginswith|endswith|gt|ge|lt|le|hasvalue|novalue, " +
            "value: \"Т1\"} combined with logic=and|or. categories accept OST_ names or localized " +
            "names. dryRun defaults to true; dryRun=false requires user confirmation. The answer " +
            "contains per-section status, changed/skipped/failed items and read-back per view.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""name"":{""type"":""string""},""categories"":{""type"":""array"",""minItems"":1,""maxItems"":10,""items"":{""type"":""string""}},""conditions"":{""type"":""array"",""maxItems"":10,""items"":{""type"":""object"",""properties"":{""field"":{""type"":""string"",""description"":""category|family|type|level|room|system|parameter""},""parameter"":{""type"":""string""},""op"":{""type"":""string"",""default"":""eq""},""value"":{""type"":[""string"",""number"",""boolean""]}},""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""visibility"":{""type"":""string"",""enum"":[""none"",""hide"",""show""],""default"":""none""},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""viewIds"",""categories""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var sections = new List<SectionReport>();
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        var categories = AgentJson.GetStringArray(args, "categories");
        var conditions = ReadConditions(args, outcome);
        var logic = (AgentJson.GetString(args, "logic") ?? "and").Trim().ToLowerInvariant();
        var visibility = (AgentJson.GetString(args, "visibility") ?? "none").Trim().ToLowerInvariant();
        var requestedName = AgentJson.GetString(args, "name");
        var atomic = AgentJson.GetBool(args, "atomic", false);

        if (outcome.Success == false)
        {
            Finish(outcome, sections);
            return;
        }

        if (viewIds.Count == 0 || viewIds.Count > MaxViews)
        {
            Fail(outcome, "invalid_arguments",
                "viewIds must contain 1.." + MaxViews + " view ids.", sections);
            return;
        }

        if (categories.Count == 0 || categories.Count > MaxCategories)
        {
            Fail(outcome, "invalid_arguments",
                "categories must contain 1.." + MaxCategories + " category names.", sections);
            return;
        }

        if (logic is not ("and" or "or"))
        {
            Fail(outcome, "invalid_arguments", "logic must be 'and' or 'or'.", sections);
            return;
        }

        if (visibility is not ("none" or "hide" or "show"))
        {
            Fail(outcome, "invalid_arguments",
                "visibility must be 'none', 'hide' or 'show'.", sections);
            return;
        }

        if (conditions.Count > AgentFilterRuleBuilder.MaxConditions)
        {
            Fail(outcome, "invalid_arguments",
                "At most " + AgentFilterRuleBuilder.MaxConditions + " conditions per filter.",
                sections);
            return;
        }

        var unknownCategories = new List<string>();
        var categoryIds =
            AgentCategoryResolver.Resolve(doc, categories, unknownCategories);
        if (unknownCategories.Count > 0)
        {
            sections.Add(Section("categories", "failed",
                "unknown: " + string.Join(", ", unknownCategories)));
            outcome.Data["unknownCategories"] = unknownCategories;
            Fail(outcome, "unknown_categories",
                "Unknown categories: " + string.Join(", ", unknownCategories) +
                ". Use list_categories to see the names.", sections);
            return;
        }

        if (categoryIds.Count == 0)
        {
            sections.Add(Section("categories", "failed", "no filterable categories resolved"));
            Fail(outcome, "invalid_arguments", "No filterable categories resolved.", sections);
            return;
        }

        sections.Add(Section("categories", "ok", categoryIds.Count + " category(ies)"));

        var build = AgentFilterRuleBuilder.Build(doc, categoryIds, conditions, logic);
        if (!build.Ok)
        {
            sections.Add(Section("rules", "failed", build.Reason));
            outcome.Data["availableParameters"] = build.AvailableParameters;
            Fail(outcome, build.ErrorCode!, build.Reason ?? "The filter rules are invalid.",
                sections);
            return;
        }

        if (build.Filter is not null && !IsAcceptable(doc, categoryIds, build.Filter, out var why))
        {
            sections.Add(Section("rules", "failed", why));
            Fail(outcome, "filter_not_acceptable", why ?? "Revit rejected the filter rules.",
                sections);
            return;
        }

        sections.Add(Section("rules", "ok", conditions.Count + " condition(s), logic=" + logic));

        var elementFilter = build.Filter;
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? BuildDefaultName(doc, categoryIds, conditions)
            : requestedName!.Trim();

        var sanitized = SanitizeName(name);
        if (!string.Equals(sanitized, name, StringComparison.Ordinal))
        {
            outcome.Warnings.Add(
                "The filter name contains characters Revit forbids; it was changed from '" +
                name + "' to '" + sanitized + "'.");
            name = sanitized;
            outcome.Data["nameSanitized"] = true;
        }

        var filter = AgentFilterWriteHelper.FindByName(doc, name);
        var created = false;
        var reused = false;

        if (filter is not null && CategoriesMatch(filter, categoryIds))
        {
            reused = true;
            sections.Add(Section("filter", "skipped",
                "an existing filter '" + filter.Name + "' with the same categories is reused"));
        }
        else
        {
            if (filter is not null)
                name = AgentFilterWriteHelper.UniqueName(doc, name);

            try
            {
                var categoryList = new List<ElementId>(categoryIds);
                filter = elementFilter is null
                    ? ParameterFilterElement.Create(doc, name, categoryList)
                    : ParameterFilterElement.Create(doc, name, categoryList, elementFilter);
                created = true;
            }
            catch (Exception ex)
            {
                sections.Add(Section("filter", "failed", ex.Message));
                Fail(outcome, "filter_create_failed",
                    "Revit could not create the filter: " + ex.Message, sections);
                return;
            }

            sections.Add(Section("filter", "ok", "created '" + filter.Name + "'"));
        }

        if (created)
        {
            outcome.Items.Add(new ChangeItem
            {
                ElementId = filter.Id.GetValue(),
                UniqueId = filter.UniqueId,
                ParameterName = "filter_created",
                NewValue = filter.Name,
                Status = ChangeStatus.Applied
            });
        }

        var filterId = filter.Id;
        var readBack = new List<object>();
        var anyFailure = false;

        foreach (var viewId in viewIds)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = viewId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, viewId, out var view, out var code, out var reason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, code, reason);
                anyFailure = true;
                if (ApplyViewTemplateTool.Abort(outcome, atomic))
                {
                    Finish(outcome, sections, readBack);
                    return;
                }

                continue;
            }

            report.Name = view.Name;

            if (!AgentFilterWriteHelper.SupportsFilters(view))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "unsupported_view_type",
                    "View '" + view.Name + "' (" + view.ViewType +
                    ") does not support view filters.");
                anyFailure = true;
                if (ApplyViewTemplateTool.Abort(outcome, atomic))
                {
                    Finish(outcome, sections, readBack);
                    return;
                }

                continue;
            }

            if (AgentFilterWriteHelper.IsFilterControlLockedByTemplate(view, out var templateId))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "view_controlled_by_template",
                    "The view template " + templateId + " controls V/G overrides; change the " +
                    "template instead of the view.");
                anyFailure = true;
                if (ApplyViewTemplateTool.Abort(outcome, atomic))
                {
                    Finish(outcome, sections, readBack);
                    return;
                }

                continue;
            }

            var before = AgentFilterWriteHelper.ReadState(view);
            var beforeJson = AgentFilterWriteHelper.SerializeState(before);

            try
            {
                var applied = IsApplied(view, filterId);
                if (!applied)
                    view.AddFilter(filterId);

                if (visibility == "hide")
                    view.SetFilterVisibility(filterId, false);
                else if (visibility == "show")
                    view.SetFilterVisibility(filterId, true);
            }
            catch (Exception ex)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "filter_apply_failed",
                    "View '" + view.Name + "': " + ex.Message);
                anyFailure = true;
                if (ApplyViewTemplateTool.Abort(outcome, atomic))
                {
                    Finish(outcome, sections, readBack);
                    return;
                }

                continue;
            }

            var after = AgentFilterWriteHelper.ReadState(view);
            var afterJson = AgentFilterWriteHelper.SerializeState(after);

            readBack.Add(new
            {
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                filterId = filterId.GetValue(),
                filterName = filter.Name,
                applied = true,
                visible = ReadVisibility(view, filterId),
                overrides = DescribeEntry(after, filterId)
            });

            if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            {
                report.Reason = "The filter is already applied with the same visibility (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            outcome.Items.Add(new ChangeItem
            {
                ElementId = view.Id.GetValue(),
                UniqueId = view.UniqueId,
                ParameterName = "filter",
                OldValue = beforeJson,
                NewValue = afterJson,
                Status = ChangeStatus.Applied
            });
            report.NewValue = filter.Name;
            report.Detail = "visible=" + ReadVisibility(view, filterId);
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
        }

        sections.Add(Section("views", anyFailure ? "failed" : "ok",
            outcome.Batch.Changed.Count + " changed, " + outcome.Batch.Skipped.Count +
            " skipped, " + outcome.Batch.Failed.Count + " failed"));

        outcome.Data["filterId"] = filterId.GetValue();
        outcome.Data["filterName"] = filter.Name;
        outcome.Data["created"] = created;
        outcome.Data["reused"] = reused;
        outcome.Data["visibility"] = visibility;
        Finish(outcome, sections, readBack);
    }

    private static List<FilterConditionSpec> ReadConditions(
        JsonElement args,
        WriteOutcome outcome)
    {
        var conditions = new List<FilterConditionSpec>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("conditions", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return conditions;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_arguments";
                outcome.Message = "Each condition must be an object.";
                return conditions;
            }

            var spec = new FilterConditionSpec
            {
                Field = AgentJson.GetString(item, "field"),
                Parameter = AgentJson.GetString(item, "parameter"),
                Op = AgentJson.GetString(item, "op") ?? "eq"
            };

            if (item.TryGetProperty("value", out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                spec.Value = value.Clone();
                spec.HasValue = true;
            }

            conditions.Add(spec);
        }

        return conditions;
    }

    private static bool IsAcceptable(
        Document doc,
        IReadOnlyList<ElementId> categories,
        ElementFilter filter,
        out string? reason)
    {
        reason = null;
        try
        {
            var set = new HashSet<ElementId>(categories);
            if (ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(
                    doc, set, filter))
            {
                return true;
            }

            reason = "The rule set is not acceptable for these categories: a rule references a " +
                     "parameter that does not apply to the categories.";
            return false;
        }
        catch (Exception ex)
        {
            reason = "Revit rejected the filter rules: " + ex.Message;
            return false;
        }
    }

    private static bool CategoriesMatch(
        ParameterFilterElement filter,
        IReadOnlyList<ElementId> categoryIds)
    {
        try
        {
            var existing = filter.GetCategories();
            if (existing.Count != categoryIds.Count)
                return false;

            foreach (var id in categoryIds)
            {
                var found = false;
                foreach (var candidate in existing)
                {
                    if (candidate.GetValue() == id.GetValue())
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsApplied(View view, ElementId filterId)
    {
        foreach (var id in view.GetFilters())
        {
            if (id.GetValue() == filterId.GetValue())
                return true;
        }

        return false;
    }

    private static bool ReadVisibility(View view, ElementId filterId)
    {
        try
        {
            return view.GetFilterVisibility(filterId);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static ViewOverrideState DescribeEntry(ViewFilterState state, ElementId filterId)
    {
        foreach (var entry in state.Filters)
        {
            if (entry.FilterId == filterId.GetValue())
                return entry.Overrides;
        }

        return new ViewOverrideState();
    }

    private static readonly char[] ForbiddenNameChars =
    {
        '\\', '/', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '\'', '~', '"'
    };

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var symbol in name)
        {
            builder.Append(Array.IndexOf(ForbiddenNameChars, symbol) >= 0 ? '-' : symbol);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "SmartCon filter" : result;
    }

    private static string BuildDefaultName(
        Document doc,
        IReadOnlyList<ElementId> categoryIds,
        IReadOnlyList<FilterConditionSpec> conditions)
    {
        var builder = new StringBuilder("Фильтр ");
        var first = true;
        foreach (var id in categoryIds)
        {
            var category = Category.GetCategory(doc, id);
            var label = category?.Name ?? id.GetValue().ToString(CultureInfo.InvariantCulture);
            if (!first)
                builder.Append(", ");
            builder.Append(label);
            first = false;

            if (builder.Length > 60)
                break;
        }

        if (conditions.Count > 0)
        {
            var condition = conditions[0];
            builder.Append(" — ");
            builder.Append(condition.Field ?? condition.Parameter ?? "parameter");
            builder.Append(' ');
            builder.Append(condition.Op);
            if (condition.HasValue && condition.Value.ValueKind == JsonValueKind.String)
                builder.Append(' ').Append(condition.Value.GetString());
        }

        if (builder.Length > 90)
            return builder.ToString(0, 87) + "...";

        return builder.ToString();
    }

    private static SectionReport Section(string section, string status, string? reason) =>
        new() { Section = section, Status = status, Reason = reason };

    private static void Fail(
        WriteOutcome outcome,
        string errorCode,
        string message,
        List<SectionReport> sections)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = message;
        Finish(outcome, sections);
    }

    private static void Finish(
        WriteOutcome outcome,
        List<SectionReport> sections,
        IReadOnlyList<object>? readBack = null)
    {
        outcome.Data["sections"] = sections.Count > 0 ? sections : null;
        if (readBack is not null)
            outcome.Data["readBack"] = readBack;
    }
}