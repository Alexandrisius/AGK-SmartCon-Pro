using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Moves views/sheets into a project-browser sub-folder (#348). There is no
/// BuiltInParameter.GROUP_TITLE and no direct folder API: the folder of a
/// view is the value of the grouping parameter of the CURRENT browser
/// organization (BrowserOrganization.GetFolderItems), so the tool writes that
/// parameter. When the organization groups by a read-only property (the
/// default "type" grouping), the tool answers
/// browser_folder_parameter_read_only instead of failing silently.
/// </summary>
public sealed class SetBrowserFolderTool : AgentWriteToolBase
{
    private const int MaxViews = 100;

    public SetBrowserFolderTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_browser_folder",
        Description =
            "Moves views and/or sheets into a project-browser sub-folder: sets the grouping " +
            "parameter of the CURRENT browser organization (e.g. the 'Раздел'/'Sub-Discipline' " +
            "project parameter) of every target. folder='' removes the view from its folder. " +
            "Views and sheets are grouped by different organizations — the tool picks the right " +
            "one per element. On a multi-level scheme the folder value is written to the NEAREST " +
            "WRITABLE grouping level (leaf first, then upwards); the parameter actually used is " +
            "named per item (detail) and in parameterName. " +
            "Guards: browser_folder_parameter_read_only (every grouping level of the active " +
            "organization is read-only and listed in the message — reorganize the " +
            "browser around a writable parameter first), view_not_found. undo_last restores the " +
            "previous values. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""integer""},""description"":""View ids (list_views)""},""viewNames"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""string""},""description"":""View names, when ids are unknown""},""sheetIds"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""integer""},""description"":""Sheet ids (list_sheets)""},""folder"":{""type"":""string"",""description"":""Sub-folder name; '' removes the target from its folder""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""folder""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (!args.TryGetProperty("folder", out _))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass folder (the sub-folder name; '' removes the folder).";
            return;
        }

        var folder = AgentJson.GetString(args, "folder")?.Trim() ?? string.Empty;
        var viewIds = AgentJson.GetLongArray(args, "viewIds");
        var viewNames = AgentJson.GetStringArray(args, "viewNames");
        var sheetIds = AgentJson.GetLongArray(args, "sheetIds");
        if (viewIds.Count == 0 && viewNames.Count == 0 && sheetIds.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass viewIds[], viewNames[] or sheetIds[] (1.." + MaxViews + ").";
            return;
        }

        if (viewIds.Count + viewNames.Count + sheetIds.Count > MaxViews)
        {
            outcome.Success = false;
            outcome.ErrorCode = "limit_exceeded";
            outcome.Message = "At most " + MaxViews + " targets per call; split the request.";
            return;
        }

        var viewsOrganization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
        var sheetsOrganization = BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc);
        outcome.Description = "Set browser folder '" + folder + "'";
        outcome.Data["folder"] = folder;

        var seen = new HashSet<long>();
        var usedParameters = new HashSet<string>(StringComparer.Ordinal);
        var failed = 0;
        var changed = 0;

        foreach (var target in ResolveTargets(doc, viewIds, viewNames, sheetIds, seen, outcome))
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport
            {
                Id = target.Id.GetValue(),
                Name = target.Name,
                Detail = target is ViewSheet ? "sheet" : "view"
            };

            var organization = target is ViewSheet ? sheetsOrganization : viewsOrganization;
            Parameter? parameter = null;
            string? reason = null;
            var readOnlyOnly = false;
            if (organization is null
                || !TryGetGroupingParameter(doc, organization, target, out parameter, out reason, out readOnlyOnly))
            {
                report.ErrorCode = readOnlyOnly
                    ? "browser_folder_parameter_read_only"
                    : "browser_folder_unavailable";
                report.Reason = reason ?? "The document has no browser organization.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            usedParameters.Add(ParameterName(doc, parameter));
            report.Detail = (target is ViewSheet ? "sheet" : "view") +
                            "; parameter '" + ParameterName(doc, parameter) + "'";

            if (parameter.IsReadOnly)
            {
                report.ErrorCode = "browser_folder_parameter_read_only";
                report.Reason =
                    "The active browser organization groups '" + target.Name + "' by the " +
                    "read-only parameter '" + ParameterName(doc, parameter) +
                    "'. Reorganize the browser around a writable view parameter " +
                    "(Browser Organization in the Revit UI) and retry.";
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            var oldValue = parameter.AsString() ?? string.Empty;
            if (string.Equals(oldValue, folder, StringComparison.Ordinal))
            {
                report.Reason = "The view is already in the folder" +
                                (folder.Length == 0 ? " (no folder)." : " '" + folder + "'.");
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (dryRun)
            {
                report.OldValue = oldValue;
                report.NewValue = folder;
                report.Reason = "Dry run: the parameter would become '" + folder + "'.";
                outcome.Batch.Changed.Add(report);
                changed++;
                continue;
            }

            try
            {
                if (!parameter.Set(folder))
                {
                    report.ErrorCode = "set_failed";
                    report.Reason = "Revit refused the folder value '" + folder + "'.";
                    outcome.Batch.Failed.Add(report);
                    failed++;
                    continue;
                }
            }
            catch (Exception ex)
            {
                report.ErrorCode = "set_failed";
                report.Reason = ex.GetType().Name + ": " + ex.Message;
                outcome.Batch.Failed.Add(report);
                failed++;
                continue;
            }

            report.OldValue = oldValue;
            report.NewValue = folder;
            outcome.Batch.Changed.Add(report);
            changed++;

            outcome.Items.Add(new ChangeItem
            {
                UniqueId = target.UniqueId,
                ElementId = target.Id.GetValue(),
                ParameterName = "browser_folder",
                OldValue = oldValue,
                NewValue = folder,
                Status = ChangeStatus.Applied
            });
        }

        outcome.AffectedCount = changed;
        // Present in the dry run too: the model sees which grouping level the
        // folder will be written to before any write (S.42 follow-up).
        if (usedParameters.Count > 0)
        {
            outcome.Data["resolvedParameter"] = string.Join(" / ", usedParameters);
        }

        if (changed > 0)
        {
            outcome.Data["parameterName"] = string.Join(" / ", usedParameters);
        }

        if (failed > 0)
        {
            outcome.Success = false;
            if (changed == 0)
            {
                // Promote the dominant per-item code when every target failed for
                // the same reason (the model sees browser_folder_parameter_read_only,
                // not a generic partial_failure).
                var codes = outcome.Batch.Failed
                    .Select(item => item.ErrorCode)
                    .Where(code => !string.IsNullOrEmpty(code))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (codes.Count == 1)
                    outcome.ErrorCode = codes[0];
            }

            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = changed + " target(s) moved, " + failed + " failed. See failed[].";
        }
    }

    /// <summary>Five-arg overload for callers that only need the parameter or the reason.</summary>
    internal static bool TryGetGroupingParameter(
        Document doc,
        BrowserOrganization organization,
        View target,
        out Parameter parameter,
        out string? reason) =>
        TryGetGroupingParameter(doc, organization, target, out parameter, out reason, out _);

    /// <summary>
    /// The nearest writable grouping parameter of the current organization for
    /// this element. The folder the element sits in is the deepest level of the
    /// scheme, but when it is read-only (e.g. «Семейство») the folder value must
    /// be written on the nearest writable level above it (e.g. the stamp-section
    /// parameter) — the view still moves (S.42). The rejection lists every
    /// grouping level with its writability instead of naming only the leaf.
    /// </summary>
    internal static bool TryGetGroupingParameter(
        Document doc,
        BrowserOrganization organization,
        View target,
        out Parameter parameter,
        out string? reason,
        out bool readOnlyOnly)
    {
        parameter = null!;
        reason = null;
        readOnlyOnly = false;

        try
        {
            var items = organization.GetFolderItems(target.Id);
            if (items is null || items.Count == 0)
            {
                reason = "The browser organization has no folder level for '" + target.Name + "'.";
                return false;
            }

            // All grouping levels of the leaf path, shallow → deep. R26+ carries
            // the full path on the leaf item; the older API exposes one folder
            // item per level with its own parameter id.
            var parameterIds = new List<ElementId>();
#if REVIT2026_OR_GREATER
            var path = items[items.Count - 1].GetGroupingParameterIdPath();
            if (path is not null)
                parameterIds.AddRange(path);
#else
            // GetGroupingParameterIdPath exists from Revit 2026 only; the older
            // API exposes the folder parameter id per level directly (not
            // obsolete there).
            foreach (var item in items)
            {
                if (item.ElementId is not null)
                    parameterIds.Add(item.ElementId);
            }
#endif

            if (parameterIds.Count == 0)
            {
                reason = "The browser organization does not group '" + target.Name +
                         "' by a parameter.";
                return false;
            }

            var readOnly = new List<string>();
            for (var index = parameterIds.Count - 1; index >= 0; index--)
            {
                var parameterId = parameterIds[index];

                // The ElementId overload of Element.Parameter exists only in newer
                // versions; the Parameters scan works everywhere (same trick as
                // SetViewParametersTool).
                Parameter? found = null;
                foreach (Parameter candidate in target.Parameters)
                {
                    if (candidate.Id.GetValue() == parameterId.GetValue())
                    {
                        found = candidate;
                        break;
                    }
                }

                if (found is null)
                {
                    readOnly.Add("level " + (index + 1) + ": not bound to '" + target.Name + "'");
                    continue;
                }

                if (!found.IsReadOnly)
                {
                    parameter = found;
                    return true;
                }

                readOnly.Add("level " + (index + 1) + ": '" + ParameterName(doc, found) +
                             "' (read-only)");
            }

            reason =
                "Every grouping level of the browser organization is not writable for '" +
                target.Name + "': " + string.Join(", ", readOnly) +
                ". Reorganize the browser around a writable view parameter " +
                "(Browser Organization in the Revit UI) and retry.";
            readOnlyOnly = true;
            return false;
        }
        catch (Exception ex)
        {
            reason = "The browser folder could not be resolved: " +
                     ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static IEnumerable<View> ResolveTargets(
        Document doc,
        IReadOnlyList<long> viewIds,
        IReadOnlyList<string> viewNames,
        IReadOnlyList<long> sheetIds,
        HashSet<long> seen,
        WriteOutcome outcome)
    {
        foreach (var sheetId in sheetIds)
        {
            var view = ResolveOne(doc, sheetId, null, seen, out var code, out var reason,
                requireSheet: true);
            if (view is null)
                ReportReject(outcome, sheetId, code, reason);
            else
                yield return view;
        }

        foreach (var viewId in viewIds)
        {
            var view = ResolveOne(doc, viewId, null, seen, out var code, out var reason,
                requireSheet: false);
            if (view is null)
                ReportReject(outcome, viewId, code, reason);
            else
                yield return view;
        }

        if (viewNames.Count > 0)
        {
            var names = AgentViewWriteHelper.CollectViewNamesByName(doc);
            foreach (var viewName in viewNames)
            {
                if (!names.TryGetValue(viewName, out var foundId))
                {
                    ReportReject(outcome, null, "view_not_found",
                        "No view named '" + viewName + "' exists in the document.");
                    continue;
                }

                var view = ResolveOne(doc, foundId, viewName, seen, out var code, out var reason,
                    requireSheet: false);
                if (view is null)
                {
                    ReportReject(outcome, foundId, code, reason);
                    continue;
                }

                yield return view;
            }
        }
    }

    private static View? ResolveOne(
        Document doc,
        long viewId,
        string? nameHint,
        HashSet<long> seen,
        out string? errorCode,
        out string? reason,
        bool requireSheet)
    {
        errorCode = null;
        reason = null;

        var element = doc.GetElement(ElementIdCompat.Create(viewId));
        if (element is not View view)
        {
            errorCode = "view_not_found";
            reason = "No view with id " + viewId + " exists in the document.";
            return null;
        }

        if (requireSheet && view is not ViewSheet)
        {
            errorCode = "not_a_sheet";
            reason = "Element " + viewId + " is not a sheet.";
            return null;
        }

        if (!requireSheet && view is ViewSheet)
        {
            errorCode = "not_a_view";
            reason = "Element " + viewId + " is a sheet; pass it in sheetIds[].";
            return null;
        }

        if (view.IsTemplate)
        {
            errorCode = "view_is_template";
            reason = "View '" + (nameHint ?? view.Name) + "' is a view template.";
            return null;
        }

        if (!seen.Add(viewId))
        {
            errorCode = "duplicate_item";
            reason = "View " + viewId + " is listed more than once in this call.";
            return null;
        }

        return view;
    }

    private static string ParameterName(Document doc, Parameter parameter) =>
        parameter.Definition?.Name ?? ("id " + parameter.Id.GetValue());

    private static void ReportReject(
        WriteOutcome outcome,
        long? id,
        string? errorCode,
        string? reason)
    {
        outcome.Batch.Failed.Add(new WriteItemReport
        {
            Id = id,
            ErrorCode = errorCode,
            Reason = reason
        });
    }
}
