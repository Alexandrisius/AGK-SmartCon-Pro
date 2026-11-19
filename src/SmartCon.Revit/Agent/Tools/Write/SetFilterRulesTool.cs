using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Replaces the rules of an existing parameter view filter. Categories can be
/// replaced in the same call. The previous rules are not restorable by
/// undo_last (Revit exposes no printable rule set), so the tool reports that.
/// </summary>
public sealed class SetFilterRulesTool : AgentWriteToolBase
{
    public SetFilterRulesTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_filter_rules",
        Description =
            "Replaces the rules of an existing view filter by filterId or filterName. Conditions " +
            "use the same DSL as create_view_filter ({field: category|family|type|level|room|" +
            "system|parameter, parameter, op, value} with logic=and|or); an empty conditions " +
            "array clears all rules. categories (names) optionally replaces the filter's " +
            "categories. Dry run by default. The previous rule set cannot be restored by " +
            "undo_last; the tool reports undoSupported=false.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""filterId"":{""type"":""integer""},""filterName"":{""type"":""string""},""categories"":{""type"":""array"",""items"":{""type"":""string""}},""conditions"":{""type"":""array"",""maxItems"":10,""items"":{""type"":""object"",""properties"":{""field"":{""type"":""string""},""parameter"":{""type"":""string""},""op"":{""type"":""string"",""default"":""eq""},""value"":{""type"":[""string"",""number"",""boolean""]}},""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var filterId = AgentJson.GetLong(args, "filterId", 0);
        var filterName = AgentJson.GetString(args, "filterName");
        var filter = filterId > 0
            ? AgentFilterWriteHelper.FindById(doc, filterId)
            : null;
        if (filter is null && !string.IsNullOrWhiteSpace(filterName))
            filter = AgentFilterWriteHelper.FindByName(doc, filterName);

        if (filter is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "filter_not_found";
            outcome.Message = "Pass filterId or filterName of an existing view filter " +
                              "(see list_filters).";
            return;
        }

        var conditions = AgentFilterConditionReader.Read(args, outcome);
        if (!outcome.Success)
            return;

        if (conditions.Count > AgentFilterRuleBuilder.MaxConditions)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "At most " + AgentFilterRuleBuilder.MaxConditions +
                              " conditions per filter.";
            return;
        }

        var logic = (AgentJson.GetString(args, "logic") ?? "and").Trim().ToLowerInvariant();
        if (logic is not ("and" or "or"))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "logic must be 'and' or 'or'.";
            return;
        }

        List<ElementId> categoryIds;
        try
        {
            categoryIds = new List<ElementId>(filter.GetCategories());
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "filter_read_failed";
            outcome.Message = "The filter categories could not be read: " + ex.Message;
            return;
        }

        var requestedCategories = AgentJson.GetStringArray(args, "categories");
        if (requestedCategories.Count > 0)
        {
            var unknown = new List<string>();
            var resolved = AgentCategoryResolver.Resolve(doc, requestedCategories, unknown);
            if (unknown.Count > 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "unknown_categories";
                outcome.Message = "Unknown categories: " + string.Join(", ", unknown) +
                                  ". Use list_categories to see the names.";
                return;
            }

            if (resolved.Count == 0)
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_arguments";
                outcome.Message = "No filterable categories resolved.";
                return;
            }

            categoryIds = new List<ElementId>(resolved);
        }

        ElementFilter? elementFilter = null;
        if (conditions.Count > 0)
        {
            var build = AgentFilterRuleBuilder.Build(doc, categoryIds, conditions, logic);
            if (!build.Ok)
            {
                outcome.Success = false;
                outcome.ErrorCode = build.ErrorCode ?? "invalid_arguments";
                outcome.Message = build.Reason ?? "The filter rules are invalid.";
                outcome.Data["availableParameters"] = build.AvailableParameters;
                return;
            }

            elementFilter = build.Filter;
            if (elementFilter is not null && !IsAcceptable(doc, categoryIds, elementFilter, out var why))
            {
                outcome.Success = false;
                outcome.ErrorCode = "filter_not_acceptable";
                outcome.Message = why ?? "Revit rejected the filter rules.";
                return;
            }
        }

        var previous = DescribePreviousState(doc, filter);
        var oldSummary = previous.Summary;
        var report = new WriteItemReport
        {
            Id = filter.Id.GetValue(),
            Name = filter.Name,
            OldValue = oldSummary
        };

        // The rules are not restorable by undo_last, so the answer itself must carry
        // the old tree: parameterId + parameterName, operator and value per rule
        // (external run 6, P1).
        outcome.Data["previousRules"] = previous.Rules;
        outcome.Data["previousCategories"] = previous.CategoryNames;
        outcome.Data["previousCategoryIds"] = previous.CategoryIds;

        if (dryRun)
        {
            report.NewValue = "rules=" + (elementFilter is null ? 0 : AgentFilterRuleDescriber.CountRules(elementFilter)) +
                              ", categories=" + categoryIds.Count;
            report.Reason = "Dry run: rules would be replaced. The current rule tree is in " +
                            "previousRules; nothing was changed.";
            outcome.Batch.Changed.Add(report);
            outcome.Data["wouldUpdate"] = true;
            outcome.Data["undoSupported"] = false;
            return;
        }

        try
        {
            if (requestedCategories.Count > 0)
                filter.SetCategories(new List<ElementId>(categoryIds));

            if (elementFilter is null)
                filter.ClearRules();
            else if (!filter.SetElementFilter(elementFilter))
            {
                outcome.Success = false;
                outcome.ErrorCode = "filter_rules_rejected";
                outcome.Message = "Revit refused the new rule set for filter '" + filter.Name + "'.";
                return;
            }
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "filter_update_failed";
            outcome.Message = "Revit refused to update filter '" + filter.Name + "': " + ex.Message;
            return;
        }

        report.NewValue = DescribeState(doc, filter);
        report.Detail = elementFilter is null
            ? "all rules cleared; categories=" + categoryIds.Count
            : "rules replaced; categories=" + categoryIds.Count;
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            ElementId = filter.Id.GetValue(),
            UniqueId = filter.UniqueId,
            ParameterName = "filter_rules",
            OldValue = oldSummary,
            NewValue = report.NewValue,
            Status = ChangeStatus.Applied
        });

        // Rule text is not reversible through the change set: suppress it instead of
        // letting undo_last report a rollback it cannot perform.
        outcome.SuppressChangeSet = true;
        outcome.Data["undoSupported"] = false;
        outcome.Warnings.Add(
            "Filter rules were replaced. undo_last cannot restore the previous rule set; " +
            "the previous tree is in previousRules (parameterId/parameterName/op/value) — " +
            "pass its conditions back to set_filter_rules to revert.");
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
            if (ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(
                    doc,
                    new HashSet<ElementId>(categories),
                    filter))
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

    private static PreviousFilterState DescribePreviousState(
        Document doc,
        ParameterFilterElement filter)
    {
        var categoryIds = new List<long>();
        var categoryNames = new List<string>();
        try
        {
            foreach (var categoryId in filter.GetCategories())
            {
                categoryIds.Add(categoryId.GetValue());
                categoryNames.Add(
                    Category.GetCategory(doc, categoryId)?.Name ??
                    categoryId.GetValue().ToString());
            }
        }
        catch (Exception)
        {
            // Reported as an empty category list.
        }

        ElementFilter? elementFilter = null;
        try
        {
            elementFilter = filter.GetElementFilter();
        }
        catch (Exception)
        {
            // Reported as zero rules.
        }

        var ruleCount = AgentFilterRuleDescriber.CountRules(elementFilter);
        return new PreviousFilterState
        {
            Summary = "rules=" + ruleCount + ", categories=" + categoryIds.Count,
            Rules = AgentFilterRuleDescriber.Describe(elementFilter, doc),
            CategoryIds = categoryIds,
            CategoryNames = categoryNames
        };
    }

    private static string DescribeState(Document doc, ParameterFilterElement filter)
    {
        var categories = 0;
        try
        {
            categories = filter.GetCategories().Count;
        }
        catch (Exception)
        {
            // Reported as zero.
        }

        ElementFilter? elementFilter = null;
        try
        {
            elementFilter = filter.GetElementFilter();
        }
        catch (Exception)
        {
            // Reported as zero rules.
        }

        return "rules=" + AgentFilterRuleDescriber.CountRules(elementFilter) +
               ", categories=" + categories;
    }

    private sealed class PreviousFilterState
    {
        public required string Summary { get; init; }

        public required Dictionary<string, object?> Rules { get; init; }

        public required List<long> CategoryIds { get; init; }

        public required List<string> CategoryNames { get; init; }
    }
}