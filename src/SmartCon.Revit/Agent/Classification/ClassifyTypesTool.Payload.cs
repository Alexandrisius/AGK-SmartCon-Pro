using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Classification;

public sealed partial class ClassifyTypesTool
{
    private static string SetParametersNote(int calls) =>
        "setParameters holds only status=proposed items and is ready for set_parameters (dryRun=true " +
        "first). When this result is trimmed, do not read it - pass the items by reference: " +
        (calls <= 1
            ? "items={\"$result\":\"<resultHandle>\",\"path\":\"data/setParameters/items\"}."
            : "one set_parameters call per entry of setParameters.calls (" + calls +
              " calls of at most 500 items), items={\"$result\":\"<resultHandle>\"," +
              "\"path\":\"data/setParameters/calls/0/items\"}.");

    private static ClassifyPayload BuildPayload(
        ClassifySnapshot snapshot,
        IReadOnlyList<ClassifyFamilyGroup> groups,
        ClassifyMatchResult result,
        bool truncated,
        int familiesSkipped)
    {
        var proposed = CountOf(result, ClassifyTypeStatus.Proposed);
        var lowConfidence = CountOf(result, ClassifyTypeStatus.LowConfidence);
        var noConfidence = CountOf(result, ClassifyTypeStatus.NoConfidence);
        var noCandidates = CountOf(result, ClassifyTypeStatus.NoCandidates);
        var alreadySet = CountOf(result, ClassifyTypeStatus.AlreadySet);

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
                selected = snapshot.Types.Count,
                skippedAlreadySet = snapshot.SkippedAlreadySet,
                truncated
            },
            ["summary"] = new
            {
                proposed,
                lowConfidence,
                noConfidence,
                noCandidates,
                alreadySet,
                familiesProcessed = result.FamiliesProcessed,
                familiesSkipped,
                decisionCalls = result.DecisionCalls
            },
            ["alreadySet"] = result.AlreadySetItems.Select(item => new
            {
                typeId = item.Type.ElementId,
                family = item.Type.FamilyName,
                type = item.Type.TypeName,
                currentCode = item.Type.CurrentCode
            }).ToList(),
            // The report to the user: a family with its code and the path in the
            // table. Type ids live in setParameters only.
            ["assignments"] = result.Assignments.Select(group => new
            {
                family = group.Family,
                category = group.Category,
                code = group.Code,
                breadcrumb = group.Breadcrumb,
                score = System.Math.Round(group.Score, 4),
                typeCount = group.TypeIdCount
            }).ToList(),
            ["uncertain"] = UncertainGroups(result),
            ["setParameters"] = setParameters,
            ["setParametersNote"] = setParameters is null
                ? null
                : SetParametersNote(result.SetParameterBatches.Count),
            ["decisionUsed"] = result.DecisionUsed
        };

        if (truncated)
        {
            payload["hint"] = "The selection was cut by maxTypes/maxFamilies. Narrow it with " +
                              "categories or familyIds and classify the rest in another call.";
        }

        string? note = null;
        if (snapshot.Types.Count == 0)
        {
            note = snapshot.SkippedAlreadySet > 0
                ? "Nothing to classify: every selected type already has a classification code " +
                  "(pass onlyEmpty=false to re-classify them; their current codes are reported)."
                : "Nothing to classify: no element type with the Assembly Code parameter " +
                  "matches the selection filters. Loosen categories/familyIds/typeIds/nameContains.";
            payload["note"] = note;
        }

        if (result.Incomplete)
        {
            payload["incomplete"] = true;
            payload["notProcessed"] = result.NotProcessed;
        }

        var uncertain = lowConfidence + noConfidence + noCandidates;
        var message = "Proposed a code for " + proposed + " of " + snapshot.Types.Count +
                      " selected type(s) across " + groups.Count +
                      (groups.Count == 1 ? " family" : " families") + "; " + uncertain +
                      " uncertain.";
        if (alreadySet > 0)
        {
            message += " " + alreadySet +
                       " type(s) already carry the proposed code (status already_set).";
        }

        if (result.Incomplete)
        {
            message += " INCOMPLETE: the decision layer did not answer " +
                       result.NotProcessed.Count + " question(s) - do not report the result " +
                       "as complete.";
        }

        if (note is not null)
            message = note;

        return new ClassifyPayload(
            AgentJson.Serialize(payload),
            message,
            proposed);
    }

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
