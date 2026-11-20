using System.Globalization;
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
/// The dry run applies the change inside the transaction and rolls it back, so
/// a green dry run means Revit itself accepted the new rule set (#400).
/// </summary>
public sealed class SetFilterRulesTool : AgentWriteToolBase
{
    private const string UndoWarning =
        "undo_last не откатит это изменение. Откат — повторный вызов set_filter_rules с " +
        "conditions из previousRules и categories из previousCategories.";

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
            "use the same DSL as create_view_filter ({field: family|type|level|room|" +
            "system|parameter, parameter, op, value, unit} with logic=and|or); an empty conditions " +
            "array clears all rules, while omitting conditions changes only the categories and " +
            "leaves the rules untouched. Numeric values are converted with the Revit unit API: pass " +
            "unit=\"mm\" (or cm|m|ft|in|m2|m3|raw) or omit it to use the parameter's project " +
            "display unit; a unit of the wrong dimension is rejected with invalid_condition. " +
            "categories (names) optionally replaces the filter's " +
            "categories. dryRun=true applies the change inside the transaction and rolls it " +
            "back: validated=true means Revit accepted the new categories and rule set. A " +
            "parameter that is not filterable for every target category fails with " +
            "parameter_not_supported_for_categories before the model is touched. Identical " +
            "rules and categories return success with errorCode=no_changes. The previous rule " +
            "set cannot be restored by undo_last; the tool reports undoSupported=false.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""filterId"":{""type"":""integer""},""filterName"":{""type"":""string""},""categories"":{""type"":""array"",""items"":{""type"":""string""}},""conditions"":{""type"":""array"",""maxItems"":10,""items"":{""type"":""object"",""properties"":{""field"":{""type"":""string"",""enum"":[""family"",""type"",""level"",""room"",""system"",""parameter""]},""parameter"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""eq"",""neq"",""contains"",""notcontains"",""beginswith"",""notbeginswith"",""endswith"",""notendswith"",""gt"",""ge"",""lt"",""le"",""hasvalue"",""novalue""],""default"":""eq""},""value"":{""type"":[""string"",""number"",""boolean""]},""unit"":{""type"":""string"",""description"":""Numeric only: mm|cm|m|ft|in|m2|m3|raw. Default = the parameter's project display unit (same rule as query_elements).""}},""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
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

        // "conditions omitted" (change categories only) and "conditions: []"
        // (clear every rule) must not be the same request: the first one used to
        // wipe all rules, and undo_last cannot restore them.
        var conditionsSpecified = HasProperty(args, "conditions");

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
        }

        // Pre-flight (#400): parameter × category pair, checked before the model
        // is touched at all. ElementFilterIsAcceptableForParameterFilterElement
        // alone accepted an incompatible set in the field.
        var previous = DescribePreviousState(doc, filter);
        var filterForPreflight = conditionsSpecified ? elementFilter : previous.ElementFilter;
        var unsupported = AgentFilterPreflight.FindUnsupported(doc, categoryIds, filterForPreflight);
        if (unsupported.Count > 0)
        {
            FailUnsupported(outcome, unsupported);
            return;
        }

        // The rules are not restorable by undo_last, so the answer itself must carry
        // the old tree: parameterId + parameterName, operator and value per rule
        // (external run 6, P1).
        outcome.Data["previousRules"] = previous.Rules;
        if (ContainsRawNumericRule(previous.Rules))
        {
            outcome.Warnings.Add(
                "Numeric previousRules[].value is in Revit internal units (unit=\"raw\"). " +
                "When restoring these rules pass unit=\"raw\", otherwise the number is read " +
                "in project display units and the threshold changes.");
        }

        outcome.Data["previousCategories"] = previous.CategoryNames;
        outcome.Data["previousCategoryIds"] = previous.CategoryIds
            .Select(id => id.GetValue())
            .ToList();

        var newDigest = AgentFilterRuleDescriber.Digest(filterForPreflight, categoryIds);
        var currentDigest = AgentFilterRuleDescriber.Digest(previous.ElementFilter, categoryIds);
        if (string.Equals(newDigest, currentDigest, StringComparison.Ordinal)
            && SameCategories(previous.CategoryIds, categoryIds))
        {
            outcome.Success = true;
            outcome.ErrorCode = "no_changes";
            outcome.Message = "The filter '" + filter.Name + "' already has these rules and " +
                              "categories; nothing was changed.";
            outcome.Data["validated"] = true;
            outcome.Data["wouldUpdate"] = false;
            outcome.Data["undoSupported"] = false;
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Id = filter.Id.GetValue(),
                Name = filter.Name,
                OldValue = previous.Summary,
                Reason = "Identical rules and categories (no_changes)."
            });
            return;
        }

        var report = new WriteItemReport
        {
            Id = filter.Id.GetValue(),
            Name = filter.Name,
            OldValue = previous.Summary
        };

        string? rejection = null;
        Exception? failure = null;
        try
        {
            if (requestedCategories.Count > 0)
                filter.SetCategories(new List<ElementId>(categoryIds));

            if (conditionsSpecified)
            {
                if (elementFilter is null)
                {
                    filter.ClearRules();
                }
                else if (!filter.SetElementFilter(elementFilter))
                {
                    rejection = "Revit отклонил набор правил для фильтра '" + filter.Name + "'.";
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (rejection is not null || failure is not null)
        {
            // The categories may already be changed in this transaction: abort so
            // the base class rolls the whole call back (success=false must never
            // leave a half-applied filter behind).
            outcome.Aborted = true;
            outcome.Success = false;
            outcome.ErrorCode = "filter_rules_rejected";
            outcome.Message = failure is null
                ? rejection!
                : "Revit не смог обновить фильтр '" + filter.Name + "': " + failure.Message;
            outcome.Data["appliedSteps"] = new List<string>();
            outcome.Data["skippedSteps"] = new[]
            {
                new
                {
                    step = "rules",
                    reason = "filter_rules_rejected",
                    detail = outcome.Message
                }
            };
            outcome.Warnings.Add(
                "Транзакция откатывается целиком: категории и правила фильтра остались " +
                "прежними. Проверьте параметр через list_filterable_parameters.");
            return;
        }

        // Read-back on the Revit thread: the answer must describe what Revit
        // accepted, not what the builder intended.
        var afterFilter = SafeElementFilter(filter);
        var afterCategories = SafeCategories(filter);
        var ruleCount = AgentFilterRuleDescriber.CountRules(afterFilter);

        report.NewValue = "rules=" + ruleCount + ", categories=" + afterCategories.Count;
        report.Detail = elementFilter is null
            ? "all rules cleared; categories=" + afterCategories.Count
            : "rules replaced; categories=" + afterCategories.Count;

        outcome.Data["validated"] = true;
        outcome.Data["wouldUpdate"] = dryRun;
        outcome.Data["undoSupported"] = false;
        outcome.Data["resultRuleCount"] = ruleCount;
        outcome.Data["resultCategories"] = afterCategories
            .Select(id => new
            {
                id = id.GetValue(),
                name = Category.GetCategory(doc, id)?.Name
            })
            .ToList();

        if (dryRun)
        {
            report.Reason = "Dry run: change applied inside the rolled-back transaction; " +
                            "nothing was saved.";
            outcome.Batch.Changed.Add(report);
            outcome.Data["wouldUpdate"] = true;
            outcome.Warnings.Add(UndoWarning);
            return;
        }

        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            ElementId = filter.Id.GetValue(),
            UniqueId = filter.UniqueId,
            ParameterName = "filter_rules",
            OldValue = previous.Summary,
            NewValue = report.NewValue,
            Status = ChangeStatus.Applied
        });

        // Rule text is not reversible through the change set: suppress it instead of
        // letting undo_last report a rollback it cannot perform.
        outcome.SuppressChangeSet = true;
        outcome.Warnings.Add(
            "Правила фильтра заменены. undo_last не восстановит прежний набор: он лежит в " +
            "previousRules — передайте его обратно в conditions set_filter_rules, чтобы " +
            "вернуть как было.");
    }

    private static void FailUnsupported(
        WriteOutcome outcome,
        IReadOnlyList<UnsupportedFilterParameter> unsupported)
    {
        var first = unsupported[0];
        var categories = new List<Dictionary<string, object?>>();
        var seen = new HashSet<long>();
        foreach (var usage in unsupported)
        {
            foreach (var pair in usage.UnsupportedCategories)
            {
                if (!seen.Add(pair.Id))
                    continue;

                categories.Add(new Dictionary<string, object?>
                {
                    ["id"] = pair.Id,
                    ["name"] = pair.Name
                });
            }
        }

        var names = new List<string>();
        foreach (var row in categories)
        {
            names.Add(row["name"] as string ??
                      Convert.ToString(row["id"], CultureInfo.InvariantCulture) ?? "?");
        }

        outcome.Success = false;
        outcome.ErrorCode = "parameter_not_supported_for_categories";
        outcome.Message = "Параметр «" + (first.ParameterName ?? first.ParameterId.ToString()) +
                          "» нельзя использовать в фильтре для категорий: " +
                          string.Join(", ", names) +
                          ". Сузьте набор категорий или выберите другой параметр.";
        outcome.Data["details"] = new Dictionary<string, object?>
        {
            ["parameter"] = first.ParameterName,
            ["parameterId"] = first.ParameterId,
            ["unsupportedCategories"] = categories
        };
        outcome.Data["allUnsupported"] = unsupported
            .Select(usage => new
            {
                parameter = usage.ParameterName,
                parameterId = usage.ParameterId,
                categoryCount = usage.UnsupportedCategories.Count
            })
            .ToList();
        outcome.Data["hint"] = "list_filterable_parameters(categoryIds) показывает параметры, " +
                               "совместимые со всеми категориями.";
    }

    private static PreviousFilterState DescribePreviousState(
        Document doc,
        ParameterFilterElement filter)
    {
        var categoryIds = new List<ElementId>();
        var categoryNames = new List<string>();
        try
        {
            foreach (var categoryId in filter.GetCategories())
            {
                categoryIds.Add(categoryId);
                categoryNames.Add(
                    Category.GetCategory(doc, categoryId)?.Name ??
                    categoryId.GetValue().ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception)
        {
            // Reported as an empty category list.
        }

        var elementFilter = SafeElementFilter(filter);
        var ruleCount = AgentFilterRuleDescriber.CountRules(elementFilter);
        return new PreviousFilterState
        {
            Summary = "rules=" + ruleCount + ", categories=" + categoryIds.Count,
            Rules = AgentFilterRuleDescriber.Describe(elementFilter, doc),
            ElementFilter = elementFilter,
            CategoryIds = categoryIds,
            CategoryNames = categoryNames
        };
    }

    private static bool ContainsRawNumericRule(object? node)
    {
        switch (node)
        {
            case Dictionary<string, object?> map:
                if (map.TryGetValue("unit", out var unit)
                    && string.Equals(unit as string, "raw", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return map.Values.Any(ContainsRawNumericRule);
            case IEnumerable<object?> items:
                return items.Any(ContainsRawNumericRule);
            default:
                return false;
        }
    }

    private static bool SameCategories(
        IReadOnlyList<ElementId> left,
        IReadOnlyList<ElementId> right)
    {
        if (left.Count != right.Count)
            return false;

        var ids = new HashSet<long>();
        foreach (var id in left)
            ids.Add(id.GetValue());

        foreach (var id in right)
        {
            if (!ids.Contains(id.GetValue()))
                return false;
        }

        return true;
    }

    private static bool HasProperty(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        if (args.TryGetProperty(name, out _))
            return true;

        foreach (var property in args.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ElementFilter? SafeElementFilter(ParameterFilterElement filter)
    {
        try
        {
            return filter.GetElementFilter();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<ElementId> SafeCategories(ParameterFilterElement filter)
    {
        try
        {
            return new List<ElementId>(filter.GetCategories());
        }
        catch (Exception)
        {
            return new List<ElementId>();
        }
    }

    private sealed class PreviousFilterState
    {
        public required string Summary { get; init; }

        public required Dictionary<string, object?> Rules { get; init; }

        public ElementFilter? ElementFilter { get; init; }

        public required List<ElementId> CategoryIds { get; init; }

        public required List<string> CategoryNames { get; init; }
    }
}
