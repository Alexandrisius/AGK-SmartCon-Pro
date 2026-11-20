using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class SetFilterOverridesTool : AgentWriteToolBase
{
    private const int MaxViews = 50;

    public SetFilterOverridesTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_filter_overrides",
        Description =
            "Sets graphic overrides (colour, fill pattern, transparency, halftone) of an existing " +
            "view filter and applies the filter to the views. Identify the filter by filterId or " +
            "exact filterName; call create_view_filter first when the filter does not exist yet. " +
            "'color' paints surfaces and lines in one key. dryRun defaults to true; dryRun=false " +
            "requires user confirmation. The answer contains per-section status, changed/skipped/" +
            "failed items and read-back of the applied overrides.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""filterId"":{""type"":""integer""},""filterName"":{""type"":""string""},""color"":{""type"":""string""},""projectionLineColor"":{""type"":""string""},""cutLineColor"":{""type"":""string""},""surfaceForegroundColor"":{""type"":""string""},""surfaceBackgroundColor"":{""type"":""string""},""pattern"":{""type"":""string""},""transparency"":{""type"":""integer"",""minimum"":0,""maximum"":100},""halftone"":{""type"":""boolean""},""clear"":{""type"":""boolean"",""default"":false},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""viewIds""],""additionalProperties"":false}"
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
        var filterId = AgentJson.GetLong(args, "filterId", 0);
        var filterName = AgentJson.GetString(args, "filterName");
        var clear = AgentJson.GetBool(args, "clear", false);
        var atomic = AgentJson.GetBool(args, "atomic", false);

        if (viewIds.Count == 0 || viewIds.Count > MaxViews)
        {
            Fail(outcome, "invalid_arguments",
                "viewIds must contain 1.." + MaxViews + " view ids.", sections);
            return;
        }

        ParameterFilterElement? filter;
        if (filterId > 0)
        {
            filter = AgentFilterWriteHelper.FindById(doc, filterId);
            if (filter is null)
            {
                sections.Add(Section("filter", "failed", "filter " + filterId + " not found"));
                outcome.Data["knownFilters"] = AgentFilterWriteHelper.KnownFilterNames(doc, 30);
                Fail(outcome, "filter_not_found",
                    "Element " + filterId + " is not a view filter. Use create_view_filter or " +
                    "filterName.", sections);
                return;
            }
        }
        else if (!string.IsNullOrWhiteSpace(filterName))
        {
            filter = AgentFilterWriteHelper.FindByName(doc, filterName);
            if (filter is null)
            {
                sections.Add(Section("filter", "failed",
                    "no filter named '" + filterName + "'"));
                outcome.Data["knownFilters"] = AgentFilterWriteHelper.KnownFilterNames(doc, 30);
                Fail(outcome, "filter_not_found",
                    "No filter named '" + filterName + "'. Create it with create_view_filter " +
                    "first; knownFilters lists the existing names.", sections);
                return;
            }
        }
        else
        {
            Fail(outcome, "invalid_arguments",
                "Pass filterId or filterName to identify the filter.", sections);
            return;
        }

        sections.Add(Section("filter", "ok", "'" + filter.Name + "' (" + filter.Id.GetValue() + ")"));

        if (clear && HasAnyOverride(args))
        {
            Fail(outcome, "invalid_arguments",
                "clear=true cannot be combined with colour/pattern/transparency settings.", sections);
            return;
        }

        if (!clear && !HasAnyOverride(args))
        {
            Fail(outcome, "invalid_arguments",
                "Nothing to change: pass at least one of color, projectionLineColor, cutLineColor, " +
                "surfaceForegroundColor, surfaceBackgroundColor, pattern, transparency, halftone " +
                "or clear=true.", sections);
            return;
        }

        if (!TryBuildTarget(doc, args, clear, out var target, out var targetError, out var knownPatterns))
        {
            sections.Add(Section("overrides", "failed", targetError));
            if (knownPatterns.Count > 0)
                outcome.Data["knownPatterns"] = knownPatterns;
            Fail(outcome, "invalid_overrides", targetError ?? "The overrides are invalid.", sections);
            return;
        }

        sections.Add(Section("overrides", "ok", Describe(target)));

        var readBack = new List<object>();
        var anyFailure = false;
        var filterElementId = filter.Id;
        var warnedDisabledFilter = false;

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
                    ") does not support filter overrides.");
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
                var settings = clear
                    ? new OverrideGraphicSettings()
                    : BuildSettings(view, filterElementId, target);
                view.SetFilterOverrides(filterElementId, settings);
            }
            catch (Exception ex)
            {
                ApplyViewTemplateTool.Fail(outcome, report, "override_apply_failed",
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

            // Overrides on a disabled filter are stored but invisible: the user
            // would see "applied" and no change on screen.
            if (!warnedDisabledFilter)
            {
                try
                {
                    if (!view.GetIsFilterEnabled(filterElementId))
                    {
                        warnedDisabledFilter = true;
                        outcome.Warnings.Add(
                            "The filter '" + filter.Name + "' is assigned to view '" + view.Name +
                            "' but disabled there: the overrides have no visible effect. " +
                            "[Action: enable the filter (create_view_filter visibility=show/hide " +
                            "or the V/G dialog) and retry]");
                    }
                }
                catch (Exception)
                {
                    // The enabled flag is not readable for this view; skip the hint.
                }
            }

            var entry = FindEntry(after, filterElementId);

            readBack.Add(new
            {
                viewId = view.Id.GetValue(),
                viewName = view.Name,
                filterId = filterElementId.GetValue(),
                filterName = filter.Name,
                applied = true,
                overrides = entry?.Overrides ?? new ViewOverrideState()
            });

            if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
            {
                report.Reason = "The overrides already match the requested state (no-op).";
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
            report.Detail = Describe(entry?.Overrides ?? new ViewOverrideState());
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
        }

        sections.Add(Section("views", anyFailure ? "failed" : "ok",
            outcome.Batch.Changed.Count + " changed, " + outcome.Batch.Skipped.Count +
            " skipped, " + outcome.Batch.Failed.Count + " failed"));

        outcome.Data["filterId"] = filterElementId.GetValue();
        outcome.Data["filterName"] = filter.Name;
        Finish(outcome, sections, readBack);
    }

    private static bool HasAnyOverride(JsonElement args) =>
        !string.IsNullOrWhiteSpace(AgentJson.GetString(args, "color"))
        || !string.IsNullOrWhiteSpace(AgentJson.GetString(args, "projectionLineColor"))
        || !string.IsNullOrWhiteSpace(AgentJson.GetString(args, "cutLineColor"))
        || !string.IsNullOrWhiteSpace(AgentJson.GetString(args, "surfaceForegroundColor"))
        || !string.IsNullOrWhiteSpace(AgentJson.GetString(args, "surfaceBackgroundColor"))
        || !string.IsNullOrWhiteSpace(AgentJson.GetString(args, "pattern"))
        || args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty("transparency", out _)
        || args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty("halftone", out _);

    private static bool TryBuildTarget(
        Document doc,
        JsonElement args,
        bool clear,
        out ViewOverrideState target,
        out string? error,
        out IReadOnlyList<string> knownPatterns)
    {
        target = new ViewOverrideState();
        error = null;
        knownPatterns = Array.Empty<string>();

        if (clear)
            return true;

        var colorText = AgentJson.GetString(args, "color");
        if (!string.IsNullOrWhiteSpace(colorText))
        {
            if (!AgentFilterWriteHelper.TryParseColor(colorText!, out var color, out error))
                return false;

            target.SurfaceForegroundColor = AgentFilterWriteHelper.FormatColor(color);
            target.ProjectionLineColor = target.SurfaceForegroundColor;
            target.CutLineColor = target.SurfaceForegroundColor;

            var solid = AgentFilterWriteHelper.FindPattern(doc, "solid");
            if (solid is null)
            {
                error = "The document has no solid fill pattern to paint surfaces.";
                return false;
            }

            target.SurfaceForegroundPatternId = solid.Id.GetValue();
        }

        if (!TryColor(args, "projectionLineColor", out var projection, ref error))
            return false;
        target.ProjectionLineColor = projection ?? target.ProjectionLineColor;

        if (!TryColor(args, "cutLineColor", out var cut, ref error))
            return false;
        target.CutLineColor = cut ?? target.CutLineColor;

        if (!TryColor(args, "surfaceForegroundColor", out var surface, ref error))
            return false;
        target.SurfaceForegroundColor = surface ?? target.SurfaceForegroundColor;

        if (!TryColor(args, "surfaceBackgroundColor", out var background, ref error))
            return false;
        target.SurfaceBackgroundColor = background ?? target.SurfaceBackgroundColor;

        var patternName = AgentJson.GetString(args, "pattern");
        if (!string.IsNullOrWhiteSpace(patternName))
        {
            var pattern = AgentFilterWriteHelper.FindPattern(doc, patternName);
            if (pattern is null)
            {
                knownPatterns = AgentFilterWriteHelper.KnownPatternNames(doc, 30);
                error = "Fill pattern '" + patternName + "' was not found. " +
                        "Use 'solid' or a name from knownPatterns.";
                return false;
            }

            target.SurfaceForegroundPatternId = pattern.Id.GetValue();

            if (string.IsNullOrWhiteSpace(target.SurfaceForegroundColor))
            {
                var fallback = new Color(90, 90, 90);
                target.SurfaceForegroundColor = AgentFilterWriteHelper.FormatColor(fallback);
            }
        }

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("transparency", out var transparency)
            && transparency.ValueKind == JsonValueKind.Number)
        {
            target.Transparency = Math.Max(0, Math.Min(100, transparency.GetInt32()));
        }

        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("halftone", out var halftone)
            && (halftone.ValueKind == JsonValueKind.True
                || halftone.ValueKind == JsonValueKind.False))
        {
            target.Halftone = halftone.GetBoolean();
        }

        return true;
    }

    private static bool TryColor(
        JsonElement args,
        string property,
        out string? target,
        ref string? error)
    {
        target = null;
        var text = AgentJson.GetString(args, property);
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!AgentFilterWriteHelper.TryParseColor(text!, out var color, out error))
            return false;

        target = AgentFilterWriteHelper.FormatColor(color);
        return true;
    }

    /// <summary>
    /// Delta semantics: keep the current overrides of the view and apply only the
    /// requested keys; clear=true starts from an empty override set.
    /// </summary>
    private static OverrideGraphicSettings BuildSettings(
        View view,
        ElementId filterId,
        ViewOverrideState target)
    {
        ViewOverrideState current;
        try
        {
            current = AgentFilterWriteHelper.Describe(view.GetFilterOverrides(filterId));
        }
        catch (Exception)
        {
            current = new ViewOverrideState();
        }

        current.ProjectionLineColor = target.ProjectionLineColor ?? current.ProjectionLineColor;
        current.ProjectionLinePatternId =
            target.ProjectionLinePatternId ?? current.ProjectionLinePatternId;
        current.ProjectionLineWeight =
            target.ProjectionLineWeight ?? current.ProjectionLineWeight;
        current.CutLineColor = target.CutLineColor ?? current.CutLineColor;
        current.CutLinePatternId = target.CutLinePatternId ?? current.CutLinePatternId;
        current.CutLineWeight = target.CutLineWeight ?? current.CutLineWeight;
        current.CutForegroundColor =
            target.CutForegroundColor ?? current.CutForegroundColor;
        current.CutForegroundPatternId =
            target.CutForegroundPatternId ?? current.CutForegroundPatternId;
        current.CutForegroundPatternVisible =
            target.CutForegroundPatternVisible ?? current.CutForegroundPatternVisible;
        current.CutBackgroundColor =
            target.CutBackgroundColor ?? current.CutBackgroundColor;
        current.CutBackgroundPatternId =
            target.CutBackgroundPatternId ?? current.CutBackgroundPatternId;
        current.CutBackgroundPatternVisible =
            target.CutBackgroundPatternVisible ?? current.CutBackgroundPatternVisible;
        current.SurfaceForegroundColor =
            target.SurfaceForegroundColor ?? current.SurfaceForegroundColor;
        current.SurfaceBackgroundColor =
            target.SurfaceBackgroundColor ?? current.SurfaceBackgroundColor;
        current.SurfaceForegroundPatternId =
            target.SurfaceForegroundPatternId ?? current.SurfaceForegroundPatternId;
        current.SurfaceForegroundPatternVisible =
            target.SurfaceForegroundPatternVisible ?? current.SurfaceForegroundPatternVisible;
        current.SurfaceBackgroundPatternId =
            target.SurfaceBackgroundPatternId ?? current.SurfaceBackgroundPatternId;
        current.SurfaceBackgroundPatternVisible =
            target.SurfaceBackgroundPatternVisible ?? current.SurfaceBackgroundPatternVisible;
        // Only what the caller specified: a color-only request must not reset
        // transparency/halftone that a person set earlier.
        current.Transparency = target.Transparency ?? current.Transparency;
        current.Halftone = target.Halftone ?? current.Halftone;
        return AgentFilterWriteHelper.ToSettings(current);
    }

    private static ViewFilterEntry? FindEntry(ViewFilterState state, ElementId filterId)
    {
        foreach (var entry in state.Filters)
        {
            if (entry.FilterId == filterId.GetValue())
                return entry;
        }

        return null;
    }

    private static string Describe(ViewOverrideState state)
    {
        var parts = new List<string>();
        if (state.SurfaceForegroundColor is not null)
            parts.Add("surface " + state.SurfaceForegroundColor);
        if (state.SurfaceBackgroundColor is not null)
            parts.Add("background " + state.SurfaceBackgroundColor);
        if (state.ProjectionLineColor is not null)
            parts.Add("lines " + state.ProjectionLineColor);
        if (state.CutLineColor is not null)
            parts.Add("cut " + state.CutLineColor);
        if (state.SurfaceForegroundPatternId is not null)
            parts.Add("pattern " + state.SurfaceForegroundPatternId);
        if (state.Transparency is > 0)
            parts.Add("transparency " + state.Transparency.Value.ToString(
                CultureInfo.InvariantCulture));
        if (state.Halftone is true)
            parts.Add("halftone");

        return parts.Count == 0 ? "empty overrides" : string.Join(", ", parts);
    }

    private static SectionReport Section(string section, string status, string? reason) =>
        new() { Section = section, Status = status, Reason = reason };

    private static void Finish(
        WriteOutcome outcome,
        List<SectionReport> sections,
        IReadOnlyList<object>? readBack = null)
    {
        outcome.Data["sections"] = sections.Count > 0 ? sections : null;
        if (readBack is not null)
            outcome.Data["readBack"] = readBack;
    }

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
}