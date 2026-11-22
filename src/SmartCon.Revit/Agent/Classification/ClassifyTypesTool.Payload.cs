using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Classification;

public sealed partial class ClassifyTypesTool
{
    private const string SetParametersNote =
        "Ready for set_parameters: call with dryRun=true first. Contains only status=proposed items.";

    private static ClassifyPayload BuildPayload(
        ClassifySnapshot snapshot,
        IReadOnlyList<ClassifyFamilyGroup> groups,
        ClassifyMatchResult result,
        bool truncated)
    {
        var proposed = CountOf(result, ClassifyTypeStatus.Proposed);
        var lowConfidence = CountOf(result, ClassifyTypeStatus.LowConfidence);
        var noCandidates = CountOf(result, ClassifyTypeStatus.NoCandidates);
        var alreadySet = CountOf(result, ClassifyTypeStatus.AlreadySet);

        var setParameters = SetParametersOf(result);
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
                noCandidates,
                alreadySet,
                familiesProcessed = groups.Count
            },
            ["assignments"] = result.Assignments.Select(group => new
            {
                familyId = group.FamilyId,
                family = group.Family,
                category = group.Category,
                code = group.Code,
                breadcrumb = group.Breadcrumb,
                score = System.Math.Round(group.Score, 4),
                typeIdCount = group.TypeIdCount,
                typeIds = group.TypeIds,
                types = group.TypeNames
            }).ToList(),
            ["uncertain"] = result.Types
                .Where(item => item.Status is ClassifyTypeStatus.LowConfidence
                    or ClassifyTypeStatus.NoCandidates)
                .Select(item => new Dictionary<string, object?>
                {
                    ["typeId"] = item.Type.ElementId,
                    ["family"] = item.Type.FamilyName,
                    ["type"] = item.Type.TypeName,
                    ["reason"] = item.Status == ClassifyTypeStatus.LowConfidence
                        ? "low_confidence"
                        : "no_candidates",
                    ["current"] = item.Type.CurrentCode,
                    ["score"] = item.Status == ClassifyTypeStatus.LowConfidence
                        ? System.Math.Round(item.Score, 4)
                        : null,
                    ["candidates"] = item.Candidates
                        .Select(candidate => new
                        {
                            code = candidate.Code,
                            score = System.Math.Round(candidate.Score, 4),
                            breadcrumb = candidate.Breadcrumb
                        })
                        .ToList()
                })
                .ToList(),
            ["setParameters"] = setParameters,
            ["decisionUsed"] = true
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
                : "No element type with the Assembly Code parameter matches the selection " +
                  "filters. Loosen categories/familyIds/typeIds/nameContains.";
            payload["note"] = note;
        }

        if (result.Incomplete)
        {
            payload["incomplete"] = true;
            payload["notProcessed"] = result.NotProcessed;
        }

        var uncertain = lowConfidence + noCandidates;
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

    private static object? SetParametersOf(ClassifyMatchResult result)
    {
        if (result.SetParameterBatches.Count == 0)
            return null;

        if (result.SetParameterBatches.Count == 1)
        {
            return new
            {
                items = ItemsOf(result.SetParameterBatches[0]),
                batches = 1,
                note = SetParametersNote
            };
        }

        return new
        {
            batches = result.SetParameterBatches
                .Select(batch => new { items = ItemsOf(batch) })
                .ToList(),
            note = SetParametersNote + " Spread across " + result.SetParameterBatches.Count +
                   " calls of at most 500 items."
        };
    }

    private static List<object> ItemsOf(IReadOnlyList<ClassifySetParameterItem> batch) =>
        batch.Select(item => new
        {
            elementId = item.ElementId,
            parameter = item.Parameter,
            value = item.Value,
            scope = item.Scope
        }).ToList<object>();

    private static int CountOf(ClassifyMatchResult result, ClassifyTypeStatus status) =>
        result.Types.Count(item => item.Status == status);

    private sealed record ClassifyPayload(JsonElement Data, string Message, int AffectedCount);
}
