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
            "uniqueness and handles swaps inside the batch (A→B, B→A). Revit also renames " +
            "the views whose name equals the old level name (plans/sections created for " +
            "that level) — the tool verifies that cascade does not create duplicate view " +
            "names. dryRun defaults to true and only reports the plan.",
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

        var allViews = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => view.ViewType is not (ViewType.ProjectBrowser
                or ViewType.SystemBrowser or ViewType.Internal))
            .ToList();

        var candidates = new List<Candidate>();
        var seenIds = new HashSet<long>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport { Id = item.LevelId, NewValue = item.NewName };
            if (!seenIds.Add(item.LevelId))
            {
                Reject(outcome, report, "duplicate_item",
                    "Level " + item.LevelId + " is listed more than once in this call.");
                continue;
            }

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

            candidates.Add(new Candidate
            {
                Item = item,
                Level = level,
                OriginalName = level.Name,
                NewName = newName
            });
        }

        var valid = SelectValid(doc, outcome, allViews, levelNames, candidates);
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

    /// <summary>
    /// Drops candidates whose level name cannot be freed by this batch and candidates
    /// whose view-name cascade would collide with an unrelated view.
    /// </summary>
    private static List<Candidate> SelectValid(
        Document doc,
        WriteOutcome outcome,
        IReadOnlyList<View> views,
        IReadOnlyDictionary<string, long> documentNames,
        IReadOnlyList<Candidate> candidates)
    {
        var candidateIds = new HashSet<long>(candidates.Select(candidate => candidate.Item.LevelId));
        var rejected = new Dictionary<long, (string Code, string Reason)>();
        var valid = new List<Candidate>();
        var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (documentNames.TryGetValue(candidate.NewName, out var owner)
                && owner != candidate.Item.LevelId
                && !candidateIds.Contains(owner))
            {
                rejected[candidate.Item.LevelId] = (
                    "duplicate_name",
                    "Another level already has the name '" + candidate.NewName +
                    "' (id " + owner + ").");
                continue;
            }

            if (!plannedTargets.Add(candidate.NewName))
            {
                rejected[candidate.Item.LevelId] = (
                    "duplicate_name",
                    "The name '" + candidate.NewName + "' is used by another item of this batch.");
                continue;
            }

            valid.Add(candidate);
        }

        bool removed;
        do
        {
            removed = false;
            var validIds = new HashSet<long>(valid.Select(candidate => candidate.Item.LevelId));
            for (var index = valid.Count - 1; index >= 0; index--)
            {
                var candidate = valid[index];
                if (!documentNames.TryGetValue(candidate.NewName, out var owner)
                    || owner == candidate.Item.LevelId
                    || validIds.Contains(owner))
                {
                    continue;
                }

                rejected[candidate.Item.LevelId] = (
                    "duplicate_name",
                    "The name '" + candidate.NewName + "' stays taken: its owner (id " + owner +
                    ") is not renamed by this call.");
                valid.RemoveAt(index);
                removed = true;
            }
        }
        while (removed);

        // Revit renames every view whose name equals the old level name. Simulate the
        // final state with the full accepted set (a swap frees names in the same batch),
        // then drop candidates whose cascade would collide inside one view family.
        var accepted = new List<Candidate>(valid);
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in accepted.ToList())
            {
                if (!HasCascadeConflict(doc, views, accepted, candidate))
                    continue;

                accepted.Remove(candidate);
                rejected[candidate.Item.LevelId] = (
                    "cascade_conflict",
                    "Renaming this level would also rename its views to '" + candidate.NewName +
                    "', but another view of the same family already has that name.");
                changed = true;
            }
        }
        while (changed);

        foreach (var candidate in candidates)
        {
            if (!rejected.TryGetValue(candidate.Item.LevelId, out var rejection))
                continue;

            Reject(
                outcome,
                new WriteItemReport { Id = candidate.Item.LevelId, Name = candidate.OriginalName },
                rejection.Code,
                rejection.Reason);
        }

        return accepted;
    }

    /// <summary>
    /// True when the views cascaded by <paramref name="candidate"/> would end up with
    /// the same name as another view of the same view family in the final state.
    /// Equal names across different view families (floor plan vs ceiling plan) are
    /// allowed by Revit and must not be reported.
    /// </summary>
    private static bool HasCascadeConflict(
        Document doc,
        IReadOnlyList<View> views,
        IReadOnlyList<Candidate> accepted,
        Candidate candidate)
    {
        foreach (var cascaded in views)
        {
            if (!string.Equals(cascaded.Name, candidate.OriginalName, StringComparison.Ordinal))
                continue;

            foreach (var other in views)
            {
                if (ReferenceEquals(other, cascaded))
                    continue;

                if (!SameViewFamily(doc, other, cascaded))
                    continue;

                if (string.Equals(
                        FinalName(other, accepted), candidate.NewName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string FinalName(View view, IReadOnlyList<Candidate> accepted)
    {
        foreach (var candidate in accepted)
        {
            if (string.Equals(view.Name, candidate.OriginalName, StringComparison.Ordinal))
                return candidate.NewName;
        }

        return view.Name;
    }

    private static bool SameViewFamily(Document doc, View left, View right)
    {
        var leftFamily = FamilyOf(doc, left);
        var rightFamily = FamilyOf(doc, right);
        if (leftFamily is null || rightFamily is null)
            return left.ViewType == right.ViewType;

        return leftFamily == rightFamily;
    }

    private static ViewFamily? FamilyOf(Document doc, View view) =>
        doc.GetElement(view.GetTypeId()) is ViewFamilyType familyType
            ? familyType.ViewFamily
            : null;

    private static void ApplyRenames(
        Document doc,
        IReadOnlyList<Candidate> candidates,
        WriteOutcome outcome,
        bool atomic,
        CancellationToken ct)
    {
        var entries = candidates
            .Select(candidate => new AgentRenamePlanner.Entry
            {
                Id = candidate.Item.LevelId,
                CurrentName = candidate.OriginalName,
                NewName = candidate.NewName
            })
            .ToList();

        var documentNames = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(level => level.Name)
            .ToList();

        var plan = AgentRenamePlanner.Build(entries, documentNames);
        var succeeded = new HashSet<long>();

        foreach (var entry in plan.TempFirst)
        {
            var tempLevel = doc.GetElement(ElementIdCompat.Create(entry.Id)) as Level;
            if (tempLevel is null)
                continue;

            try
            {
                tempLevel.Name = AgentRenamePlanner.TempName();
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "Could not reserve a temporary name for level " + entry.Id + ": " + ex.Message);
            }
        }

        foreach (var entry in plan.Order)
        {
            ct.ThrowIfCancellationRequested();

            var level = doc.GetElement(ElementIdCompat.Create(entry.Id)) as Level;
            var report = new WriteItemReport
            {
                Id = entry.Id,
                Name = entry.CurrentName,
                OldValue = entry.CurrentName,
                NewValue = entry.NewName
            };

            if (level is null)
            {
                report.ErrorCode = "level_not_found";
                report.Reason = "The level disappeared during the operation.";
                outcome.Batch.Failed.Add(report);
                if (AbortIfAtomic(outcome, atomic))
                    return;
                continue;
            }

            report.Detail = "Elevation " +
                            AgentUnitsFormat.ToMillimeters(level.Elevation)
                                .ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                            " mm";

            try
            {
                level.Name = entry.NewName;

                if (!string.Equals(level.Name, entry.NewName, StringComparison.Ordinal))
                {
                    report.Reason = "The level name did not change as expected.";
                    outcome.Batch.Failed.Add(report);
                    if (AbortIfAtomic(outcome, atomic))
                        return;
                    continue;
                }

                report.Name = level.Name;
                outcome.Batch.Changed.Add(report);
                outcome.AffectedCount++;
                succeeded.Add(entry.Id);

                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = level.UniqueId,
                    ElementId = level.Id.GetValue(),
                    ParameterName = "level_name",
                    OldValue = entry.CurrentName,
                    NewValue = entry.NewName,
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

        RestoreTemporaryNames(doc, plan, succeeded, outcome);

        var planned = new HashSet<long>(plan.Order.Select(entry => entry.Id));
        foreach (var candidate in candidates.Where(candidate => !planned.Contains(candidate.Item.LevelId)))
        {
            outcome.Batch.Failed.Add(new WriteItemReport
            {
                Id = candidate.Item.LevelId,
                Name = candidate.OriginalName,
                NewValue = candidate.NewName,
                ErrorCode = "duplicate_name",
                Reason = "The name '" + candidate.NewName + "' is held by an element this call " +
                         "does not rename, so the rename cannot be scheduled."
            });
        }

        if (outcome.Batch.Failed.Count > 0)
            AbortIfAtomic(outcome, atomic);
    }

    private static void RestoreTemporaryNames(
        Document doc,
        AgentRenamePlanner.Plan plan,
        HashSet<long> succeeded,
        WriteOutcome outcome)
    {
        if (outcome.Aborted)
            return;

        foreach (var entry in plan.TempFirst)
        {
            if (succeeded.Contains(entry.Id))
                continue;

            if (doc.GetElement(ElementIdCompat.Create(entry.Id)) is not Level level)
                continue;

            if (!level.Name.StartsWith(AgentRenamePlanner.TempPrefix, StringComparison.Ordinal))
                continue;

            try
            {
                level.Name = entry.CurrentName;
                outcome.Warnings.Add(
                    "Level " + entry.Id + " kept its original name '" + entry.CurrentName +
                    "' after the failed rename.");
            }
            catch (Exception ex)
            {
                outcome.Warnings.Add(
                    "Level " + entry.Id + " is left with the temporary name '" + level.Name +
                    "': " + ex.Message + " [Action: rename it manually in Revit]");
            }
        }
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

    private sealed class Candidate
    {
        public required RenameLevelItem Item { get; init; }

        public required Level Level { get; init; }

        public required string OriginalName { get; init; }

        public required string NewName { get; init; }
    }

    private sealed class RenameLevelsRequest
    {
        public List<RenameLevelItem>? Items { get; set; }
    }
}
