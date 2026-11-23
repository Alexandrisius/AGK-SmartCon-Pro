using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed partial class CreateViewFilterTool : AgentWriteToolBase
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
            "{field: family|type|level|room|system|parameter, parameter: \"Система\", " +
            "op: eq|neq|contains|notcontains|beginswith|endswith|gt|ge|lt|le|hasvalue|novalue, " +
            "value: \"В1\"} combined with logic=and|or. Numeric values are converted with the Revit unit " +
            "API: pass unit=\"mm\" (or cm|m|ft|in|m2|m3|raw) or omit it to use the parameter's " +
            "project display unit; a wrong-dimension unit (mm for an area) is rejected. " +
            "categories accept OST_ names or localized " +
            "names. Field meanings: system = system NAME/ABBREVIATION (\"ОВ1\"), not the system " +
            "classification; type = element type; «Тип системы» (подача/обратка) is a parameter — " +
            "use field=\"parameter\" with parameter=\"Классификация систем\" (or " +
            "RBS_SYSTEM_CLASSIFICATION_PARAM), value is the display string. Check the " +
            "category×parameter pair with list_filterable_parameters before writing. " +
            "dryRun defaults to true; dryRun=false requires user confirmation. The answer " +
            "contains per-section status, changed/skipped/failed items and read-back per view. The " +
            "filter is created only when at least one passed view can accept it: when every " +
            "view fails validation (template-controlled, schedule, legend, sheet), nothing is " +
            "created and failed[] names the reason per view. With atomic=false the filter is " +
            "applied to the views that can take it and the rest are reported in failed[]; " +
            "atomic=true makes the whole call all-or-nothing. Order matters with view " +
            "templates: a template that includes V/G Overrides Filters owns the filter list " +
            "of its views (own filters are replaced on assignment and dropped on any later " +
            "template change) — assign the template first or add the filters to the template. " +
            "visibility defaults to none: the filter is applied but disabled, and the result " +
            "warns (appliedButDisabled). Every view row carries action=filter_assigned; in a " +
            "dry run every filter id is a discarded preview id (filterIdPreview=true). " +
            "After the commit each view is verified to still carry the filter " +
            "(filter_not_applied in failed[] otherwise).",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""name"":{""type"":""string""},""categories"":{""type"":""array"",""minItems"":1,""maxItems"":10,""items"":{""type"":""string""}},""conditions"":{""type"":""array"",""maxItems"":10,""items"":{""type"":""object"",""properties"":{""field"":{""type"":""string"",""enum"":[""family"",""type"",""level"",""room"",""system"",""parameter""]},""parameter"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""eq"",""neq"",""contains"",""notcontains"",""beginswith"",""notbeginswith"",""endswith"",""notendswith"",""gt"",""ge"",""lt"",""le"",""hasvalue"",""novalue""],""default"":""eq""},""value"":{""type"":[""string"",""number"",""boolean""]},""unit"":{""type"":""string"",""description"":""Numeric only: mm|cm|m|ft|in|m2|m3|raw. Default = the parameter's project display unit (same rule as query_elements).""}},""additionalProperties"":false}},""logic"":{""type"":""string"",""enum"":[""and"",""or""],""default"":""and""},""visibility"":{""type"":""string"",""enum"":[""none"",""hide"",""show""],""default"":""none""},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""viewIds"",""categories""],""additionalProperties"":false}"
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
        var conditions = AgentFilterConditionReader.Read(args, outcome);
        var logic = (AgentJson.GetString(args, "logic") ?? "and").Trim().ToLowerInvariant();
        var visibility = (AgentJson.GetString(args, "visibility") ?? "none").Trim().ToLowerInvariant();
        var requestedName = AgentJson.GetString(args, "name");
        var atomic = AgentJson.GetBool(args, "atomic", false);

        if (outcome.Success == false)
        {
            Finish(outcome, sections);
            return;
        }

        if (!AgentFilterViewState.TryParse(visibility, out var mode))
        {
            Fail(outcome, "invalid_arguments",
                "visibility must be 'none', 'hide' or 'show'. 'hide' enables the filter and " +
                "hides matching elements; 'show' enables it and keeps them visible; 'none' " +
                "adds the filter but leaves it disabled, so elements are visible.", sections);
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

        // Pre-flight (#400): a rule is accepted only when EVERY target category
        // can be filtered by the parameter. ElementFilterIsAcceptableForParameterFilterElement
        // alone returned true for an incompatible set, so the pair is checked
        // explicitly before anything is created.
        var unsupported =
            AgentFilterPreflight.FindUnsupported(doc, categoryIds, build.Filter);
        if (unsupported.Count > 0)
        {
            var first = unsupported[0];
            var reason = "Параметр «" + (first.ParameterName ?? first.ParameterId.ToString()) +
                         "» нельзя использовать в фильтре для этих категорий: " +
                         DescribeUnsupported(first) +
                         ". Сузьте набор категорий или выберите другой параметр " +
                         "(list_filterable_parameters показывает совместимые).";
            sections.Add(Section("rules", "failed", reason));
            outcome.Data["details"] = FilterDetails(unsupported);
            outcome.Data["unsupportedCategories"] = UnsupportedCategoryRows(unsupported);
            Fail(outcome, "parameter_not_supported_for_categories", reason, sections);
            return;
        }

        if (build.Filter is not null && !IsAcceptable(doc, categoryIds, build.Filter, out var why))
        {
            sections.Add(Section("rules", "failed", why));
            Fail(outcome, "filter_rules_rejected", why ?? "Revit rejected the filter rules.",
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

        // Resolve and validate every view BEFORE touching the model: a view the
        // template controls must land in failed[] with its own code, and when no
        // view can take the filter nothing is created at all (external run 6, P1 —
        // a tool_failed/InvalidObjectException used to hide the real cause).
        var eligibleViewIds = new List<long>();
        var seenViewIds = new HashSet<long>();
        var validationFailed = 0;
        foreach (var viewId in viewIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!seenViewIds.Add(viewId))
                continue;

            var report = new WriteItemReport { Id = viewId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, viewId, out var view, out var code, out var reason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, code, reason);
                validationFailed++;
                continue;
            }

            report.Name = view.Name;
            if (!AgentFilterWriteHelper.SupportsFilters(view))
            {
                ApplyViewTemplateTool.Fail(outcome, report, "unsupported_view_type",
                    "View '" + view.Name + "' (" + view.ViewType +
                    ") does not support view filters.");
                validationFailed++;
                continue;
            }

            if (AgentFilterWriteHelper.IsFilterControlLockedByTemplate(view, out var templateId)
                && templateId.HasValue)
            {
                report.SuggestedViewIds = new List<long> { templateId.Value };
                ApplyViewTemplateTool.Fail(outcome, report, "view_controlled_by_template",
                    "The view template " + templateId.Value + " controls V/G overrides; change the " +
                    "template instead of the view (retry with viewIds=[" + templateId.Value + "]).");
                validationFailed++;
                continue;
            }

            eligibleViewIds.Add(viewId);
        }

        if (atomic && validationFailed > 0)
        {
            outcome.Aborted = true;
            outcome.Success = false;
            outcome.ErrorCode = "atomic_aborted";
            outcome.Message = validationFailed + " view(s) failed validation and atomic=true, " +
                              "so nothing was created or changed.";
            Finish(outcome, sections);
            return;
        }

        if (eligibleViewIds.Count == 0)
        {
            // Same shape as the dry run: every reason is in failed[], and the
            // top-level errorCode names it when all views failed the same way.
            outcome.Success = false;
            outcome.ErrorCode ??= "no_eligible_views";
            outcome.Message = "No view can accept the filter, so nothing was created (a filter is " +
                              "created only when at least one view accepts it). " +
                              "See failed[] for the per-view reason.";
            outcome.Data["created"] = false;
            outcome.Data["wouldCreate"] = false;
            outcome.Data["viewCounters"] = new
            {
                total = seenViewIds.Count,
                changed = 0,
                skipped = 0,
                failed = validationFailed
            };
            Finish(outcome, sections);
            return;
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

            sections.Add(Section("filter", "ok", dryRun
                    ? "would create '" + filter.Name + "' (dry run, rolled back)"
                    : "created '" + filter.Name + "'"));
        }

        // Cache the element data before the view loop: the rollback path deletes the
        // filter, and reading Name/UniqueId after Delete throws InvalidObjectException
        // (stress-test regression A).
        var filterId = filter.Id;
        var filterIdValue = filterId.GetValue();
        var filterName = filter.Name;
        var filterUniqueId = filter.UniqueId;

        if (created)
        {
            outcome.Items.Add(new ChangeItem
            {
                ElementId = filterIdValue,
                UniqueId = filterUniqueId,
                ParameterName = "filter_created",
                NewValue = filterName,
                Status = ChangeStatus.Applied
            });

            if (dryRun)
            {
                // The filter is part of the change in BOTH modes: the dry run used to
                // report one row less than the apply, so expectedChanges could not be
                // set for a plan step.
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Name = filterName,
                    Action = "filter_created",
                    NewValue = filterName,
                    Reason = "Dry run: the filter would be created and applied to the view(s) below."
                });
            }
            else
            {
                // The filter element is a real change even when applying it to a view
                // later fails: count it so a change set is persisted and undo_last can
                // remove it (feedback: partial failure lost the created filter).
                outcome.AffectedCount++;
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = filterIdValue,
                    Name = filterName,
                    Action = "filter_created",
                    Detail = "filter created"
                });
            }
        }

        // Read BEFORE anything is written: a template target that includes V/G
        // Overrides Filters hands its filter list to every view that uses it.
        var templateImpact = TemplateImpactReport.Collect(doc, eligibleViewIds);
        var appliedViewIds = new List<long>();

        var readBack = new List<object>();
        var anyFailure = validationFailed > 0;
        var viewChanged = 0;
        var viewSkipped = 0;
        var viewFailed = 0;

        foreach (var viewId in eligibleViewIds)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = viewId };
            if (!AgentViewWriteHelper.TryGetView(
                    doc, viewId, out var view, out var code, out var reason))
            {
                ApplyViewTemplateTool.Fail(outcome, report, code, reason);
                anyFailure = true;
                viewFailed++;
                if (ApplyViewTemplateTool.Abort(outcome, atomic))
                {
                    Finish(outcome, sections, readBack);
                    return;
                }

                continue;
            }

            report.Name = view.Name;

            // A filter on a category that is hidden in this view cannot be seen at all;
            // the model should know before it promises a colour (B8).
            var hiddenCategories = categoryIds
                .Where(id => IsCategoryHidden(view, id))
                .ToList();
            if (categoryIds.Count > 0 && hiddenCategories.Count == categoryIds.Count)
            {
                outcome.Warnings.Add(
                    "All filter categories are hidden on view '" + view.Name +
                    "' — the filter is applied but has no visible effect. Show the category " +
                    "first (set_view_visibility hidden=false).");
            }

            var before = AgentFilterWriteHelper.ReadState(view);
            var beforeJson = AgentFilterWriteHelper.SerializeState(before);

            try
            {
                var applied = IsApplied(view, filterId);
                if (!applied)
                    view.AddFilter(filterId);

                // Two independent flags (#400): visibility="none" must disable the
                // filter itself, otherwise it keeps hiding the matching elements.
                AgentFilterViewState.Apply(view, filterId, mode);
            }
            catch (Exception ex)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "filter_apply_failed",
                    "View '" + view.Name + "': " + ex.Message);
                anyFailure = true;
                viewFailed++;
                if (ApplyViewTemplateTool.Abort(outcome, atomic))
                {
                    Finish(outcome, sections, readBack);
                    return;
                }

                continue;
            }

            var after = AgentFilterWriteHelper.ReadState(view);
            var afterJson = AgentFilterWriteHelper.SerializeState(after);

            var probeId = created && dryRun;
            readBack.Add(new
            {
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                filterId = probeId ? null : (long?)filterIdValue,
                previewFilterId = probeId ? (long?)filterIdValue : null,
                filterName = filterName,
                applied = true,
                filterEnabled = AgentFilterViewState.ReadEnabled(view, filterId),
                visible = AgentFilterViewState.ReadVisible(view, filterId),
                mode = AgentFilterViewState.ModeName(
                    AgentFilterViewState.ReadEnabled(view, filterId),
                    AgentFilterViewState.ReadVisible(view, filterId)),
                overrides = DescribeEntry(after, filterId)
            });

            if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            {
                report.Reason = "The filter is already applied with the same visibility (no-op).";
                outcome.Batch.Skipped.Add(report);
                viewSkipped++;
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

            // The row says WHAT happened to the view: the filter name in newValue
            // alone read as a view rename.
            report.Action = "filter_assigned";
            report.NewValue = filterName;
            report.Detail = (probeId ? "filter (preview id " + filterIdValue + ")" : "filter id " + filterIdValue) +
                            ", " + AgentFilterViewState.Describe(view, filterId);
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
            viewChanged++;
            appliedViewIds.Add(viewId);
            if (view.IsTemplate)
                templateImpact.Changed.Add(view.Id.GetValue());
        }

        sections.Add(Section("views", anyFailure ? "failed" : "ok",
            "viewsChanged=" + viewChanged + ", viewsSkipped=" + viewSkipped +
            ", viewsFailed=" + (validationFailed + viewFailed) +
            " (the filter element itself is reported in the filter section, not here)"));

        if (dryRun && created && anyFailure && viewChanged == 0)
        {
            // The apply removes a new filter no view accepted; the dry-run row goes too,
            // so both modes report the same number of changes.
            outcome.Batch.Changed.RemoveAll(row => row.Action == "filter_created");
        }

        templateImpact.Publish(outcome);
        WarnIfAppliedButDisabled(outcome, viewChanged, mode);

        var filterCountsAsChange = created && !dryRun;
        var rolledBack = false;

        if (anyFailure && filterCountsAsChange && viewChanged == 0)
        {
            // The filter was created but no view accepted it: roll the creation back so
            // the answer is unambiguous ("nothing changed") instead of success=false
            // with an orphan filter left in the model. Name/UniqueId were cached before
            // the loop: reading them after Delete throws InvalidObjectException
            // (stress-test regression A).
            try
            {
                doc.Delete(filterId);
                rolledBack = true;
                outcome.Data["filterRolledBack"] = true;
                outcome.Warnings.Add(
                    "The filter '" + filterName + "' was created but could not be applied to " +
                    "any view, so it was removed again. Nothing was changed.");
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "The filter '" + filterName + "' was created but not applied to any view, " +
                    "and Revit refused to remove it: " + ex.Message +
                    ". Use undo_last to remove it.");
            }

            if (rolledBack)
                RemoveCreatedFilterRows(outcome, filterIdValue);
        }
        else if (anyFailure && viewChanged > 0)
        {
            // The filter and at least one view already changed: keep the partial result
            // as a success with failed[] instead of a misleading hard failure.
            outcome.Success = true;
            outcome.ErrorCode = null;
            outcome.Message = "The filter was applied to " + viewChanged +
                              " view(s); " + (validationFailed + viewFailed) +
                              " view(s) failed. See failed[] for reasons.";
        }

        outcome.Data["viewCounters"] = new
        {
            total = seenViewIds.Count,
            changed = viewChanged,
            skipped = viewSkipped,
            failed = validationFailed + viewFailed
        };

        PublishResultData(outcome, new FilterResult
        {
            FilterIdValue = filterIdValue,
            FilterName = filterName,
            Created = created,
            Reused = reused,
            RolledBack = rolledBack,
            DryRun = dryRun,
            Atomic = atomic,
            Mode = mode,
            AppliedViewIds = appliedViewIds
        });

        Finish(outcome, sections, readBack);
    }

}
