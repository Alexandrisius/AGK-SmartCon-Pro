using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Bulk view rename: selector expands to frozen units, every unit is one view.
/// Swaps and rename chains are ordered at expansion time (temp names), so the
/// apply step never hits a transient name collision.
/// </summary>
public sealed class RenameViewsJobAction : RevitJobActionBase
{
    private const string TempPrefix = "__sc_tmp_";

    public override string Name => "rename_views";

    public override JobActionScope Scope => JobActionScope.ProjectInstance;

    public override int MaxUnits => 5000;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
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

        var excludeTemplates = select.ExcludeTemplates ?? true;
        var excludeSheets = select.ExcludeSheets ?? true;
        var limit = select.Limit ?? int.MaxValue;

        var views = new List<View>();
        foreach (var view in new FilteredElementCollector(context.Document)
                     .OfClass(typeof(View))
                     .Cast<View>())
        {
            if (view.IsTemplate && excludeTemplates)
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

            if (!MatchesSelect(view, select))
            {
                continue;
            }

            views.Add(view);
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
            return new JobUnitApply
            {
                ErrorCode = "rename_failed",
                ErrorDetail = "Renaming '" + current + "' → '" + args.NewName + "' failed: " +
                              ex.GetType().Name + ": " + ex.Message
            };
        }
    }

    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        if (!TryFindByUniqueId<View>(context.Document, unit.UnitKey, out var view, out var error))
        {
            return new JobUnitVerification
            {
                Verified = false,
                ErrorCode = error!.Code,
                ErrorDetail = error.Message,
                MismatchReason = "The view no longer exists"
            };
        }

        var expected = ReadValue(preview.ExpectedJson);
        var actual = view!.Name;
        return new JobUnitVerification
        {
            Verified = string.Equals(actual, expected, StringComparison.Ordinal),
            ActualJson = Value(actual),
            MismatchReason = string.Equals(actual, expected, StringComparison.Ordinal)
                ? null
                : "Expected name '" + expected + "', the view is named '" + actual + "'"
        };
    }

    public override JobUnitUndo Undo(
        JobActionContext context,
        JobUnit unit,
        string? undoJson,
        bool dryRun)
    {
        var target = ReadValue(undoJson);
        if (target is null)
        {
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = "The unit has no recorded previous name"
            };
        }

        if (!TryFindByUniqueId<View>(context.Document, unit.UnitKey, out var view, out var error))
        {
            return new JobUnitUndo
            {
                ErrorCode = error!.Code,
                ErrorDetail = error.Message
            };
        }

        var current = view!.Name;
        if (string.Equals(current, target, StringComparison.Ordinal))
        {
            return new JobUnitUndo { Success = true, Skipped = true, ActualJson = Value(current) };
        }

        if (dryRun)
        {
            return new JobUnitUndo { Success = true, ActualJson = Value(current) };
        }

        try
        {
            view.Name = target;
            return new JobUnitUndo { Success = true, ActualJson = Value(target) };
        }
        catch (Exception ex)
        {
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackFailed,
                ErrorDetail = "Restoring '" + target + "' failed: " + ex.GetType().Name + ": " + ex.Message
            };
        }
    }

    private static bool MatchesSelect(View view, ViewsSelectSpec select)
    {
        if (select.ViewIds is { Count: > 0 } &&
            !select.ViewIds.Contains(view.Id.GetValue()))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(select.NameContains) &&
            view.Name.IndexOf(select.NameContains!, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(select.NameStartsWith) &&
            !view.Name.StartsWith(select.NameStartsWith!, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(select.NameEndsWith) &&
            !view.Name.EndsWith(select.NameEndsWith!, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (select.ViewTypes is { Count: > 0 })
        {
            var matched = false;
            foreach (var typeText in select.ViewTypes)
            {
                if (Enum.TryParse<ViewType>(typeText, true, out var type) && view.ViewType == type)
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static string ComputeName(ViewsRenameSpec rename, string oldName, int index)
    {
        var name = rename.SetName ?? oldName;

        if (string.IsNullOrWhiteSpace(rename.SetName))
        {
            if (!string.IsNullOrEmpty(rename.Find))
            {
                name = name.Replace(rename.Find!, rename.Replace ?? string.Empty);
            }

            if (!string.IsNullOrEmpty(rename.Prefix))
            {
                name = rename.Prefix + name;
            }

            if (!string.IsNullOrEmpty(rename.Suffix))
            {
                name = name + rename.Suffix;
            }
        }

        if (rename.Numbering is { } numbering)
        {
            var number = numbering.Start + index * (numbering.Step == 0 ? 1 : numbering.Step);
            var text = number.ToString(CultureInfo.InvariantCulture);
            if (numbering.Padding > 0)
            {
                text = text.PadLeft(numbering.Padding, '0');
            }

            var format = string.IsNullOrWhiteSpace(numbering.Format) ? "{base}_{n}" : numbering.Format!;
            name = format.Replace("{base}", name).Replace("{n}", text);
        }

        return name.Trim();
    }

    private static Dictionary<string, long> CollectViewNames(Document document)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>())
        {
            map[view.Name] = view.Id.GetValue();
        }

        return map;
    }

    private sealed class RenameCandidate
    {
        public required View View { get; init; }

        public required string OldName { get; init; }

        public required string NewName { get; init; }

        public string? TempName { get; set; }

        public string? Error { get; set; }
    }

    public sealed class RenameSpec
    {
        public ViewsSelectSpec? Select { get; set; }

        public ViewsRenameSpec? Rename { get; set; }
    }

    public sealed class ViewsSelectSpec
    {
        public List<long>? ViewIds { get; set; }

        public string? NameContains { get; set; }

        public string? NameStartsWith { get; set; }

        public string? NameEndsWith { get; set; }

        public List<string>? ViewTypes { get; set; }

        public bool? ExcludeTemplates { get; set; }

        public bool? ExcludeSheets { get; set; }

        public int? Limit { get; set; }
    }

    public sealed class ViewsRenameSpec
    {
        public string? Find { get; set; }

        public string? Replace { get; set; }

        public string? Prefix { get; set; }

        public string? Suffix { get; set; }

        public string? SetName { get; set; }

        public NumberingSpec? Numbering { get; set; }
    }

    public sealed class NumberingSpec
    {
        public int Start { get; set; } = 1;

        public int Step { get; set; } = 1;

        public int Padding { get; set; }

        public string? Format { get; set; }
    }

    public sealed class RenameUnitArgs
    {
        public long ViewId { get; set; }

        public string? OldName { get; set; }

        public string NewName { get; set; } = string.Empty;

        public string? TempName { get; set; }

        public string? Error { get; set; }
    }
}