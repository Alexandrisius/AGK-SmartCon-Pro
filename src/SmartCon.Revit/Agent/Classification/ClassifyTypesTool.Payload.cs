using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Classification;

public sealed partial class ClassifyTypesTool
{
    private static string SetParametersNote(int calls) =>
        "setParameters holds only status=proposed items and is ready for set_parameters (dryRun=true " +
        "first). Do not read or retype it - pass the items by reference " +
        "(resultHandle is always present): " +
        (calls <= 1
            ? "items={\"$result\":\"<resultHandle>\",\"path\":\"data/setParameters/items\"}."
            : "one set_parameters call per entry of setParameters.calls (" + calls +
              " calls of at most 500 items), items={\"$result\":\"<resultHandle>\"," +
              "\"path\":\"data/setParameters/calls/0/items\"}.");

    private const int NotProcessedNamesCap = 100;

    private static ClassifyPayload BuildPayload(
        ClassifySnapshot snapshot,
        IReadOnlyList<ClassifyFamilyGroup> groups,
        ClassifyMatchResult result,
        bool truncated,
        int familiesTotal,
        IReadOnlyList<ClassifyFamilyGroup> notProcessed,
        int questionBudget,
        int questionsEstimated)
    {
        var proposed = CountOf(result, ClassifyTypeStatus.Proposed);
        var lowConfidence = CountOf(result, ClassifyTypeStatus.LowConfidence);
        var noConfidence = CountOf(result, ClassifyTypeStatus.NoConfidence);
        var noCandidates = CountOf(result, ClassifyTypeStatus.NoCandidates);
        var alreadySet = CountOf(result, ClassifyTypeStatus.AlreadySet);
        var uncertainGroups = UncertainGroups(result);
        var uncertainTypes = lowConfidence + noConfidence + noCandidates;
        var processedTypes = groups.Sum(group => group.Types.Count);

        // set_parameters takes at most 500 items per call: {items} for one
        // call, {calls:[{items}…]} above. The note travels NEXT TO the
        // argument, never inside it.
        var setParameters = result.BuildSetParametersPayload();
        var payload = new Dictionary<string, object?>
        {
            ["source"] = new
            {
                loaded = true,
                entries = snapshot.Table.Entries.Count,
                leafTotal = snapshot.Table.Entries.Count(entry => entry.IsLeaf)
            },
            ["selection"] = new
            {
                typesTotal = snapshot.TypesTotal,
                skippedNotWritable = snapshot.SkippedNotWritable,
                skippedAlreadySet = snapshot.SkippedAlreadySet,
                selected = snapshot.Types.Count,
                processed = processedTypes,
                familiesTotal,
                truncated
            },
            ["summary"] = new
            {
                proposed,
                lowConfidence,
                noConfidence,
                noCandidates,
                alreadySet,
                uncertainTypes,
                uncertainGroups = uncertainGroups.Count,
                familiesSelected = groups.Count,
                familiesProcessed = result.FamiliesProcessed,
                familiesSkipped = notProcessed.Count,
                decisionCalls = result.DecisionCalls,
                questionBudget,
                questionsEstimated
            },
            ["alreadySet"] = result.AlreadySetItems.Select(item => new
            {
                typeId = item.Type.ElementId,
                family = item.Type.FamilyName,
                type = item.Type.TypeName,
                currentCode = item.Type.CurrentCode
            }).ToList(),
            // The report to the user: a family with its code and the path in the
            // table. typeIds let the model correct single types without reading
            // setParameters; the values to write live in setParameters only.
            // systemFamily marks groups without a loadable Family: their typeIds
            // are system types (WallType, …) - list_family_types resolves them in
            // systemTypes[], list_families never lists them (live case 5, R11).
            ["assignments"] = result.Assignments.Select(group => new
            {
                family = group.Family,
                category = group.Category,
                code = group.Code,
                breadcrumb = group.Breadcrumb,
                score = System.Math.Round(group.Score, 4),
                typeIds = group.TypeIds,
                systemFamily = group.FamilyId is null ? true : (bool?)null
            }).ToList(),
            ["uncertain"] = uncertainGroups,
            ["setParameters"] = setParameters,
            ["setParametersNote"] = setParameters is null
                ? null
                : SetParametersNote(result.SetParameterBatches.Count),
            ["decisionUsed"] = result.DecisionUsed
        };

        if (truncated)
        {
            // The families left out by the budgets, named and addressed: loaded
            // families by familyIds, system families (no familyId of their own)
            // by systemTypeIds. Types are summed before the cap.
            payload["notProcessed"] = new
            {
                families = notProcessed.Count,
                types = NotProcessedTypes(notProcessed),
                familyIds = notProcessed.Take(NotProcessedNamesCap)
                    .Where(family => family.FamilyId is not null)
                    .Select(family => family.FamilyId)
                    .ToList(),
                familyNames = notProcessed.Take(NotProcessedNamesCap)
                    .Select(family => family.FamilyName)
                    .ToList(),
                categories = notProcessed
                    .Select(family => family.Category)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                systemTypeIds = notProcessed
                    .Where(family => family.FamilyId is null)
                    .SelectMany(family => family.Types)
                    .Select(type => type.ElementId)
                    .ToList(),
                namesTruncated = notProcessed.Count > NotProcessedNamesCap
            };
            payload["hint"] = "The selection was cut by maxTypes/maxFamilies; the families left " +
                              "out are listed in notProcessed. Raise the limits or classify them in " +
                              "another call with their familyIds and systemTypeIds.";
        }

        string? note = null;
        if (snapshot.Types.Count == 0)
        {
            note = snapshot.SkippedNotWritable > 0 && snapshot.SkippedAlreadySet > 0
                ? "Nothing to classify: every matching type is already accounted for - " +
                  snapshot.SkippedAlreadySet + " already carry a classification code (pass " +
                  "onlyEmpty=false to re-classify them) and " + snapshot.SkippedNotWritable +
                  " cannot store the Assembly Code (tags, view types, styles and the like). " +
                  "Tell the user which elements are classified and which cannot be."
                : snapshot.SkippedNotWritable > 0
                    ? "Nothing to classify: every matching type (" + snapshot.SkippedNotWritable +
                      ") cannot store the Assembly Code - tags, view types, styles and the like never " +
                      "carry a code. Tell the user these elements are not classified."
                    : snapshot.SkippedAlreadySet > 0
                        ? "Nothing to classify: every selected type already has a classification code " +
                          "(pass onlyEmpty=false to re-classify them; their current codes are reported)."
                        : "Nothing to classify: no element type that can store the Assembly Code " +
                          "matches the selection filters. Loosen categories/familyIds/typeIds/nameContains.";
            payload["note"] = note;
        }

        if (result.Incomplete)
        {
            payload["incomplete"] = true;
            payload["notProcessedByDecision"] = result.NotProcessed;
        }

        // A cut that leaves nothing to classify is reported as a cut first: the
        // opening line the model reads must not be a proposal of "0 of 0".
        var truncation = truncated
            ? "TRUNCATED: " + notProcessed.Count + " of " + familiesTotal +
              " families (" + NotProcessedTypes(notProcessed) +
              " type(s)) were not classified - see notProcessed; raise " +
              "maxFamilies/maxTypes or classify them in another call with their familyIds " +
              "and systemTypeIds."
            : null;

        var message = truncation is not null && processedTypes == 0
            ? truncation
            : "Proposed a code for " + proposed + " of " + processedTypes +
              " type(s) across " + groups.Count +
              (groups.Count == 1 ? " family" : " families") + "; " + uncertainTypes +
              " uncertain type(s) in " + uncertainGroups.Count +
              (uncertainGroups.Count == 1 ? " group" : " groups") +
              " (uncertain is grouped by family and the candidate the model ranked first).";
        if (alreadySet > 0)
        {
            message += " " + alreadySet +
                       " type(s) already carry the proposed code (status already_set).";
        }

        if (snapshot.SkippedNotWritable > 0)
        {
            message += " Left out: " + snapshot.SkippedNotWritable +
                       " type(s) that cannot store the Assembly Code (tags, view types, styles).";
        }

        if (result.Incomplete)
        {
            message += " INCOMPLETE: the decision layer did not answer " +
                       result.NotProcessed.Count + " question(s) - do not report the result " +
                       "as complete.";
        }

        if (truncation is not null && processedTypes > 0)
        {
            message += " " + truncation;
        }

        if (note is not null)
            message = note;

        return new ClassifyPayload(
            AgentJson.Serialize(payload),
            message,
            proposed);
    }

    private static int NotProcessedTypes(IReadOnlyList<ClassifyFamilyGroup> notProcessed) =>
        notProcessed.Sum(family => family.Types.Count);

    /// <summary>
    /// The types left without a proposal, one entry per family and reason: the
    /// familyId lets the model classify the family again with other
    /// instructions, the candidates are what to offer the user.
    /// </summary>
    private static List<Dictionary<string, object?>> UncertainGroups(ClassifyMatchResult result) =>
        result.Types
            .Where(item => item.Status is ClassifyTypeStatus.LowConfidence
                or ClassifyTypeStatus.NoConfidence
                or ClassifyTypeStatus.NoCandidates)
            .GroupBy(item => new
            {
                item.Type.FamilyId,
                Reason = item.Reason ?? ReasonOf(item.Status),
                Code = item.Candidates.Count > 0 ? item.Candidates[0].Code : string.Empty
            })
            .Select(group =>
            {
                var items = group
                    .OrderBy(item => item.Type.TypeName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var noCode = items[0].Status == ClassifyTypeStatus.NoCandidates;
                return new Dictionary<string, object?>
                {
                    ["familyId"] = items[0].Type.FamilyId,
                    ["family"] = items[0].Type.FamilyName,
                    ["typeCount"] = items.Count,
                    ["typeIds"] = items.Select(item => item.Type.ElementId).ToList(),
                    ["types"] = items.Select(item => item.Type.TypeName).Take(3).ToList(),
                    ["reason"] = group.Key.Reason,
                    ["current"] = items[0].Type.CurrentCode,
                    ["score"] = noCode ? null : System.Math.Round(items.Min(item => item.Score), 4),
                    ["candidates"] = items[0].Candidates
                        .Select(candidate => new
                        {
                            code = candidate.Code,
                            score = System.Math.Round(candidate.Score, 4),
                            breadcrumb = candidate.Breadcrumb
                        })
                        .ToList()
                };
            })
            .OrderBy(group => group["family"] as string, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ReasonOf(ClassifyTypeStatus status) => status switch
    {
        ClassifyTypeStatus.LowConfidence => "low_confidence",
        ClassifyTypeStatus.NoConfidence => "chosen_without_confidence",
        _ => "no_candidates"
    };

    private static int CountOf(ClassifyMatchResult result, ClassifyTypeStatus status) =>
        result.Types.Count(item => item.Status == status);

    private sealed record ClassifyPayload(JsonElement Data, string Message, int AffectedCount);
}
