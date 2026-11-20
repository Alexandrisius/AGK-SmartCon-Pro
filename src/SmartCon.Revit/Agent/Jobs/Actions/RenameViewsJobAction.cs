using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Bulk view rename: selector expands to frozen units, every unit is one view.
/// Swaps and rename chains are ordered at expansion time (temp names), so the
/// apply step never hits a transient name collision.
/// </summary>
public sealed partial class RenameViewsJobAction : RevitJobActionBase
{
    private const string TempPrefix = "__sc_tmp_";

    public override string Name => "rename_views";

    public override JobActionScope Scope => JobActionScope.ProjectInstance;

    public override int MaxUnits => 5000;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
        var keyError = JobSpecKeyValidator.Validate(specJson, SpecRules);
        if (keyError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, keyError);
        }

        var spec = Parse<RenameSpec>(specJson);
        if (spec?.Select is null || spec.Rename is null)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "rename_views requires {select:{...}, rename:{...}}");
        }

        var select = spec.Select;
        var rename = spec.Rename;
        if (string.IsNullOrWhiteSpace(rename.SetName) &&
            string.IsNullOrWhiteSpace(rename.Find) &&
            string.IsNullOrWhiteSpace(rename.Prefix) &&
            string.IsNullOrWhiteSpace(rename.Suffix) &&
            rename.Numbering is null)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "rename must define at least one of: setName, find/replace, prefix, suffix, numbering");
        }

        var excludeSheets = select.ExcludeSheets ?? true;
        var limit = select.Limit ?? int.MaxValue;
        var includeTemplates = select.IncludeTemplates ?? (select.IsTemplate is true);
        var excludeTemplates = !includeTemplates && (select.ExcludeTemplates ?? true);

        BrowserOrganization? browserOrganization = null;
        try
        {
            browserOrganization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(context.Document);
        }
        catch (Exception)
        {
            // Folder filtering is optional; a project without an organization still
            // supports every other selector key.
        }

        var views = new List<View>();
        foreach (var view in new FilteredElementCollector(context.Document)
                     .OfClass(typeof(View))
                     .Cast<View>())
        {
            if (view.IsTemplate && excludeTemplates)
            {
                continue;
            }

            if (select.IsTemplate is { } templateOnly && view.IsTemplate != templateOnly)
            {
                continue;
            }

            if (excludeSheets && view.ViewType == ViewType.DrawingSheet)
            {
                continue;
            }

            if (view.ViewType is ViewType.Internal or ViewType.ProjectBrowser)
            {
                continue;
            }

            if (view.Name.StartsWith(TempPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!MatchesSelect(view, select, context.Document))
            {
                continue;
            }

            views.Add(view);
        }

        if (select.Folders is { Count: > 0 } || !string.IsNullOrWhiteSpace(select.Folder))
        {
            var requested = new List<string>(select.Folders ?? new List<string>());
            if (!string.IsNullOrWhiteSpace(select.Folder))
            {
                requested.Add(select.Folder!);
            }

            if (browserOrganization is null)
            {
                return ExpansionFail(
                    JobErrorCodes.InvalidSpec,
                    "select.folder was requested, but this project has no browser organization " +
                    "[Action: remove the folder filter or select views by type/name]");
            }

            views = views
                .Where(view => ListViewsTool.MatchesFolder(
                    ListViewsTool.CreateCard(context.Document, view, browserOrganization),
                    requested))
                .ToList();
        }

        views.Sort((left, right) =>
        {
            var byType = ((int)left.ViewType).CompareTo((int)right.ViewType);
            return byType != 0
                ? byType
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        if (views.Count > limit)
        {
            views.RemoveRange(limit, views.Count - limit);
        }

        var documentNames = CollectViewNames(context.Document);
        var candidates = new List<RenameCandidate>(views.Count);
        for (var index = 0; index < views.Count; index++)
        {
            var view = views[index];
            var newName = ComputeName(rename, view.Name, index);
            if (string.IsNullOrWhiteSpace(newName))
            {
                candidates.Add(new RenameCandidate
                {
                    View = view,
                    OldName = view.Name,
                    NewName = view.Name,
                    Error = "The rename rules produce an empty name"
                });
                continue;
            }

            if (newName.Length > 255)
            {
                candidates.Add(new RenameCandidate
                {
                    View = view,
                    OldName = view.Name,
                    NewName = newName,
                    Error = "The new name is longer than 255 characters"
                });
                continue;
            }

            candidates.Add(new RenameCandidate
            {
                View = view,
                OldName = view.Name,
                NewName = newName
            });
        }

        var changing = candidates
            .Where(candidate => candidate.Error is null &&
                                !string.Equals(candidate.OldName, candidate.NewName, StringComparison.Ordinal))
            .ToList();

        var plan = AgentRenamePlanner.Build(
            changing.Select(candidate => new AgentRenamePlanner.Entry
            {
                Id = candidate.View.Id.GetValue(),
                CurrentName = candidate.OldName,
                NewName = candidate.NewName
            }).ToList(),
            documentNames.Keys);

        var tempFirst = new Dictionary<long, string>();
        foreach (var entry in plan.TempFirst)
        {
            tempFirst[entry.Id] = AgentRenamePlanner.TempName();
        }

        var ordered = new List<RenameCandidate>(candidates.Count);

        foreach (var candidate in candidates.Where(candidate =>
                     candidate.Error is not null ||
                     string.Equals(candidate.OldName, candidate.NewName, StringComparison.Ordinal)))
        {
            ordered.Add(candidate);
        }

        foreach (var entry in plan.TempFirst)
        {
            var candidate = changing.First(item => item.View.Id.GetValue() == entry.Id);
            candidate.TempName = tempFirst[entry.Id];
            ordered.Add(candidate);
        }

        var tempFirstIds = new HashSet<long>(tempFirst.Keys);
        foreach (var entry in plan.Order)
        {
            if (tempFirstIds.Contains(entry.Id))
            {
                // The planner keeps temp-first entries in Order as well; the unit
                // already carries the temp name, so emitting it twice would break
                // the (job_id, unit_key) idempotency index.
                continue;
            }

            ordered.Add(changing.First(item => item.View.Id.GetValue() == entry.Id));
        }

        var blocked = changing.Where(candidate => !ordered.Contains(candidate)).ToList();
        foreach (var candidate in blocked)
        {
            candidate.Error =
                "The target name '" + candidate.NewName + "' is held by a view outside this selection";
            ordered.Add(candidate);
        }

        var warnings = new List<string>();
        if (plan.TempFirst.Count > 0)
        {
            warnings.Add(plan.TempFirst.Count + " view(s) are renamed through a temporary name to break a rename cycle");
        }

        if (rename.Numbering is not null && string.IsNullOrWhiteSpace(rename.Find) && string.IsNullOrWhiteSpace(rename.SetName))
        {
            warnings.Add(
                "numbering assigns names by position in the sorted selection: rerunning the same spec after the " +
                "names changed may produce a different mapping, so every apply needs a fresh preview");
        }

        return new JobExpansion
        {
            Units = ordered.Select(candidate => new JobUnit
            {
                Seq = 0,
                UnitKey = candidate.View.UniqueId,
                Label = candidate.OldName + " → " + candidate.NewName,
                ArgsJson = Json(new RenameUnitArgs
                {
                    ViewId = candidate.View.Id.GetValue(),
                    OldName = candidate.OldName,
                    NewName = candidate.NewName,
                    TempName = candidate.TempName,
                    Error = candidate.Error
                })
            }).ToList(),
            Warnings = warnings
        };
    }

    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        var args = Parse<RenameUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return Fail(JobErrorCodes.InvalidSpec, "Unit arguments are unreadable");
        }

        if (args.Error is not null)
        {
            return Fail(JobErrorCodes.InvalidSpec, args.Error);
        }

        if (!TryFindByUniqueId<View>(context.Document, unit.UnitKey, out var view, out var error))
        {
            return Fail(error!.Code, error.Message);
        }

        var current = view!.Name;
        var skipped = string.Equals(current, args.NewName, StringComparison.Ordinal);

        return new JobUnitPreview
        {
            Skipped = skipped,
            ExpectedJson = Value(args.NewName),
            ActualJson = Value(current),
            UndoJson = Value(current),
            Label = current + " → " + args.NewName
        };
    }

    public override JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo)
    {
        var args = Parse<RenameUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return new JobUnitApply
            {
                ErrorCode = JobErrorCodes.InvalidSpec,
                ErrorDetail = "Unit arguments are unreadable"
            };
        }

        if (!TryFindByUniqueId<View>(context.Document, unit.UnitKey, out var view, out var error))
        {
            return new JobUnitApply
            {
                ErrorCode = error!.Code,
                ErrorDetail = error.Message
            };
        }

        var current = view!.Name;
        if (string.Equals(current, args.NewName, StringComparison.Ordinal))
        {
            return new JobUnitApply
            {
                Success = true,
                Skipped = true,
                ExpectedJson = Value(args.NewName),
                UndoJson = preview.UndoJson
            };
        }

        // Preview-stale guard: the undo payload restores the value read at preview
        // time, so applying over a newer change would silently lose it. A unit in
        // a temp-name state is mid-cycle after a crash and is allowed.
        var previewActual = ReadValue(preview.ActualJson);
        if (previewActual is not null
            && !string.Equals(current, previewActual, StringComparison.Ordinal)
            && !string.Equals(current, args.TempName, StringComparison.Ordinal))
        {
            return new JobUnitApply
            {
                ErrorCode = JobErrorCodes.PreviewStale,
                ErrorDetail = JobErrors.PreviewStale(unit.UnitKey).ToMessage() +
                              " (the view is named '" + current + "')"
            };
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(args.TempName))
            {
                if (!string.Equals(current, args.TempName, StringComparison.Ordinal))
                {
                    view.Name = args.TempName!;
                }

                view.Name = args.NewName;
            }
            else
            {
                view.Name = args.NewName;
            }

            return new JobUnitApply
            {
                Success = true,
                ExpectedJson = Value(args.NewName),
                UndoJson = preview.UndoJson
            };
        }
        catch (Exception ex)
        {
            // A failure after the temp rename must not leave a __sc_tmp_ name behind.
            if (!string.Equals(view.Name, current, StringComparison.Ordinal))
            {
                try
                {
                    view.Name = current;
                }
                catch (Exception restoreEx)
                {
                    SmartConLogger.Warn(
                        "Job rename could not restore '" + current + "': " +
                        restoreEx.GetType().Name + ": " + restoreEx.Message +
                        " [Action: rename the view manually; it may still carry a __sc_tmp_ name]");
                }
            }

            return new JobUnitApply
            {
                ErrorCode = "rename_failed",
                ErrorDetail = "Renaming '" + current + "' → '" + args.NewName + "' failed: " +
                              ex.GetType().Name + ": " + ex.Message
            };
        }
    }
}
