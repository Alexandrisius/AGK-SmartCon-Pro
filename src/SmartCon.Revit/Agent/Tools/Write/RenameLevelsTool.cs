using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

public sealed class RenameLevelsTool : AgentWriteToolBase
{
    private const int MaxItems = 200;

    public RenameLevelsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rename_levels",
        Description =
            "Renames levels by an explicit levelId → newName list. Pre-checks level name " +
            "uniqueness. Revit also renames the views whose name equals the old level name " +
            "(plans/sections created for that level) — the tool verifies that cascade does " +
            "not create duplicate view names. dryRun defaults to true and only reports the plan.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""items"":{""type"":""array"",""minItems"":1,""maxItems"":200,""items"":{""type"":""object"",""properties"":{""levelId"":{""type"":""integer""},""newName"":{""type"":""string"",""minLength"":1}},""required"":[""levelId"",""newName""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true},""atomic"":{""type"":""boolean"",""default"":false}},""required"":[""items""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<RenameLevelsRequest>(args);
        var items = request?.Items;
        if (items is null || items.Count == 0)
        {
            Fail(outcome, "no_items", "The items array is empty; pass at least one {levelId,newName} pair.");
            return;
        }

        if (items.Count > MaxItems)
        {
            Fail(outcome, "limit_exceeded",
                "At most " + MaxItems + " renames per call are allowed; split the list.");
            return;
        }

        var atomic = AgentJson.GetBool(args, "atomic", false);
        var levelNames = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var allLevels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .ToList();
        foreach (var level in allLevels)
            levelNames[level.Name] = level.Id.GetValue();

        var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
        var viewNames = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in allViews)
        {
            if (view.ViewType is ViewType.ProjectBrowser or ViewType.SystemBrowser or ViewType.Internal)
                continue;
            viewNames[view.Name] = view.Id.GetValue();
        }

        var plannedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<(RenameLevelItem Item, Level Level)>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.LevelId, NewValue = item.NewName };
            if (!AgentViewWriteHelper.TryGetLevel(doc, item.LevelId, out var level, out var reason))
            {
                Reject(outcome, report, "level_not_found", reason);
                continue;
            }

            var nameError = AgentViewWriteHelper.ValidateName(item.NewName);
            if (nameError is not null)
            {
                Reject(outcome, report, "invalid_name", nameError);
                continue;
            }

            var newName = item.NewName!.Trim();
            report.Name = level.Name;
            report.OldValue = level.Name;

            if (string.Equals(level.Name, newName, StringComparison.Ordinal))
            {
                report.Reason = "The level already has this name (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (levelNames.TryGetValue(newName, out var owner) && owner != item.LevelId)
            {
                Reject(outcome, report, "duplicate_name",
                    "Another level already has the name '" + newName + "' (id " + owner + ").");
                continue;
            }

            if (!plannedNames.Add(newName))
            {
                Reject(outcome, report, "duplicate_name",
                    "The name '" + newName + "' is used by another item of this batch.");
                continue;
            }

            // Revit renames the views whose name equals the old level name; make sure
            // the cascade does not collide with an unrelated view.
            var cascadeConflict = FindCascadeConflict(allViews, level.Name, newName);
            if (cascadeConflict is not null)
            {
                Reject(outcome, report, "cascade_conflict",
                    "Renaming this level would also rename its view to '" + newName +
                    "', but view '" + cascadeConflict + "' already uses that name.");
                continue;
            }

            levelNames.Remove(level.Name);
            levelNames[newName] = item.LevelId;
            valid.Add((item, level));
        }

        if (valid.Count == 0)
        {
            outcome.Description = "rename_levels: nothing to rename";
            outcome.Message ??= "No level could be renamed; see failed[] for the reasons.";
            return;
        }

        if (atomic && outcome.Batch.Failed.Count > 0)
        {
            outcome.Aborted = true;
            outcome.Success = false;
            outcome.ErrorCode = "atomic_aborted";
            outcome.Message = outcome.Batch.Failed.Count + " item(s) failed validation and " +
                              "atomic=true, so nothing was renamed.";
            return;
        }

        outcome.Description = "Renamed " + valid.Count + " level(s)";
        ApplyRenames(doc, valid, outcome, atomic, ct);
    }

    private static void ApplyRenames(
        Document doc,
        IReadOnlyList<(RenameLevelItem Item, Level Level)> valid,
        WriteOutcome outcome,
        bool atomic,
        CancellationToken ct)
    {
        var entries = valid
            .Select(pair => new AgentRenamePlanner.Entry
            {
                Id = pair.Item.LevelId,
                CurrentName = pair.Level.Name,
                NewName = pair.Item.NewName!.Trim()
            })
            .ToList();

        foreach (var entry in AgentRenamePlanner.SelectTempRenames(entries))
        {
            var level = doc.GetElement(ElementIdCompat.Create(entry.Id)) as Level;
            if (level is not null)
                level.Name = AgentRenamePlanner.TempName();
        }

        foreach (var pair in valid)
        {
            ct.ThrowIfCancellationRequested();

            var level = pair.Level;
            var report = new WriteItemReport
            {
                Id = pair.Item.LevelId,
                NewValue = pair.Item.NewName!.Trim(),
                Detail = "Elevation " + AgentUnitsFormat.ToMillimeters(level.Elevation).ToString("F0") + " mm"
            };

            try
            {
                var oldName = level.Name;
                level.Name = pair.Item.NewName!.Trim();

                if (!string.Equals(level.Name, report.NewValue, StringComparison.Ordinal))
                {
                    report.Name = oldName;
                    report.OldValue = oldName;
                    report.Reason = "The level name did not change as expected.";
                    outcome.Batch.Failed.Add(report);
                    if (AbortIfAtomic(outcome, atomic))
                        return;
                    continue;
                }

                report.Name = level.Name;
                report.OldValue = oldName;
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;

                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = level.UniqueId,
                    ElementId = level.Id.GetValue(),
                    ParameterName = "level_name",
                    OldValue = oldName,
                    NewValue = level.Name,
                    Status = ChangeStatus.Applied
                });
            }
            catch (Exception ex)
            {
                report.ErrorCode = "rename_failed";
                report.Reason = ex.Message;
                outcome.Batch.Failed.Add(report);
                if (AbortIfAtomic(outcome, atomic))
                    return;
            }
        }
    }

    private static string? FindCascadeConflict(
        IReadOnlyList<View> views,
        string oldLevelName,
        string newLevelName)
    {
        foreach (var view in views)
        {
            if (!string.Equals(view.Name, oldLevelName, StringComparison.Ordinal))
                continue;

            foreach (var other in views)
            {
                if (ReferenceEquals(other, view))
                    continue;
                if (string.Equals(other.Name, newLevelName, StringComparison.Ordinal))
                    return other.Name;
            }
        }

        return null;
    }

    private static bool AbortIfAtomic(WriteOutcome outcome, bool atomic)
    {
        if (!atomic)
            return false;

        outcome.Aborted = true;
        outcome.Success = false;
        outcome.ErrorCode = "atomic_aborted";
        outcome.Message = "At least one rename failed and atomic=true, so the whole batch " +
                          "was rolled back. Nothing was changed.";
        return true;
    }

    private static void Reject(WriteOutcome outcome, WriteItemReport report, string code, string reason)
    {
        report.ErrorCode = code;
        report.Reason = reason;
        outcome.Batch.Failed.Add(report);
    }

    private static void Fail(WriteOutcome outcome, string code, string message)
    {
        outcome.Success = false;
        outcome.ErrorCode = code;
        outcome.Message = message;
    }

    private sealed class RenameLevelsRequest
    {
        public List<RenameLevelItem>? Items { get; set; }
    }
}
