using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Families;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Inventory of LABELS in annotation/tag family documents (марки). A label is
/// the TextElement that renders a parameter of the tagged host element. The
/// Revit API does not expose WHICH parameter a label references (CF-3799,
/// open since 2015; the read-only AnnotationLabel arrives only in Revit 2027)
/// and offers no way to re-bind it — this tool lists every label with its
/// sample text and states that boundary explicitly, so a migration plan stops
/// guessing and routes the re-labeling step to the Family Editor.
/// </summary>
public sealed class GetFamilyLabelsTool : AgentReadToolBase
{
    private const int MaxFamiliesPerCall = 20;

    private const int MaxLabelsPerFamily = 50;

    private const int MaxStaticTextSamples = 5;

    private const string ApiLimitationNote =
        "The Revit API before 2027 does not expose which parameter a label references " +
        "(CF-3799) and cannot re-bind a label: neither reading nor changing the " +
        "label→parameter link is possible through the API. Revit 2027 adds a read-only " +
        "AnnotationLabel. Re-labeling a mark stays a manual Family Editor step; to verify " +
        "that placed marks still render correctly, read the TagText of the placed tags " +
        "in the project (get_element_details on a tag element).";

    public GetFamilyLabelsTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_family_labels",
        Description =
            "Annotation/tag families (марки): lists the LABELS — the text elements that " +
            "render host-element parameters — with their sample text, plus static text " +
            "notes, opening each family document. Use it to inventory which mark families " +
            "carry labels at all before a parameter migration touches the parameters those " +
            "marks display, and to audit a sheet-title or viewport-title family. " +
            "HARD API LIMITATION (state it in the plan, do not search further): the " +
            "label→parameter link is not exposed by the Revit API before 2027 (CF-3799) — " +
            "a tool can see THAT a label exists and its sample text, but not WHICH " +
            "parameter it displays, and re-binding a label is not possible via the API " +
            "(manual Family Editor step). To verify placed marks after a migration, read " +
            "the TagText of placed tag elements in the project instead. " +
            "Batch: familyIds[] (up to 20) reads several families in one call; each entry " +
            "carries familyId, familyName, category, categoryType (annotation families " +
            "only can carry labels), labelCount, labels[] {elementId, sampleText} and " +
            "staticTextCount with a small sample. Annotation families are found with " +
            "list_families (their categories are the …Tags/…Марки ones).",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson +
            @"""familyId"":{""type"":""integer""},""familyIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":20,""description"":""Batch: read several families in one call""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        using var dialogs = AgentFamilyDialogSuppressor.BeginSafe(Context);

        var batchIds = TryGetIntArray(args, "familyIds");
        if (batchIds is { Count: > 0 })
        {
            if (batchIds.Count > MaxFamiliesPerCall)
            {
                return ToolResult.Fail(
                    "invalid_arguments",
                    "familyIds accepts up to " + MaxFamiliesPerCall + " families per call; got " +
                    batchIds.Count + ". [Action: split the list into batches]");
            }

            return ExecuteBatch(doc, batchIds, ct);
        }

        var family = AgentFamilyReader.ResolveFamily(
            doc,
            TryGetLong(args, "familyId"),
            AgentJson.GetString(args, "familyName"),
            AgentJson.GetString(args, "familyUniqueId"),
            out var errorCode,
            out var message);
        if (family is null)
            return ToolResult.Fail(errorCode ?? "family_not_found", message ?? "Family not found.");

        var (payload, familyMessage, failure) = ReadFamily(doc, family, ct);
        if (payload is null)
            return ToolResult.Fail(failure ?? "family_open_failed", familyMessage);

        payload["apiLimitation"] = ApiLimitationNote;
        return ToolResult.Ok(AgentJson.Serialize(payload), familyMessage);
    }

    private ToolResult ExecuteBatch(Document doc, IReadOnlyList<long> familyIds, CancellationToken ct)
    {
        var families = new List<object?>(familyIds.Count);
        var okCount = 0;
        var annotationCount = 0;
        foreach (var id in familyIds)
        {
            ct.ThrowIfCancellationRequested();
            var family = AgentFamilyReader.ResolveFamily(doc, id, null, null, out var errorCode, out var message);
            if (family is null)
            {
                families.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = id,
                    ["ok"] = false,
                    ["error"] = errorCode ?? "family_not_found",
                    ["message"] = message ?? "Family not found."
                });
                continue;
            }

            var (payload, familyMessage, failure) = ReadFamily(doc, family, ct);
            if (payload is null)
            {
                families.Add(new Dictionary<string, object?>
                {
                    ["familyId"] = id,
                    ["ok"] = false,
                    ["error"] = failure ?? "family_open_failed",
                    ["message"] = familyMessage
                });
                continue;
            }

            payload["ok"] = true;
            families.Add(payload);
            okCount++;
            if (payload.GetValueOrDefault("categoryType") is "annotation")
                annotationCount++;
        }

        var payloadBatch = new Dictionary<string, object?>
        {
            ["families"] = families,
            ["familyCount"] = familyIds.Count,
            ["okCount"] = okCount,
            ["failedCount"] = familyIds.Count - okCount,
            ["annotationFamilyCount"] = annotationCount,
            ["apiLimitation"] = ApiLimitationNote
        };

        var messageText = "Read labels of " + okCount + " of " + familyIds.Count + " famil(ies)" +
                          (annotationCount > 0
                              ? " (" + annotationCount + " annotation famil(ies))"
                              : "; no annotation families — labels only exist there") +
                          ". The label→parameter link is NOT readable via the API before " +
                          "Revit 2027: re-labeling marks is a manual Family Editor step.";
        return ToolResult.Ok(AgentJson.Serialize(payloadBatch), messageText);
    }

    private static (Dictionary<string, object?>? Payload, string Message, string? Failure) ReadFamily(
        Document doc,
        RevitFamily family,
        CancellationToken ct)
    {
        if (!AgentFamilyReader.TryOpenFamilyDocument(
                doc, family, out var familyDoc, out var errorCode, out var message))
        {
            return (null, message ?? "The family document could not be opened.", errorCode);
        }

        try
        {
            var labels = new List<Dictionary<string, object?>>();
            var staticTexts = new List<string>();
            var labelCount = 0;
            var staticCount = 0;
            foreach (var element in new FilteredElementCollector(familyDoc!).OfClass(typeof(TextElement)))
            {
                ct.ThrowIfCancellationRequested();
                if (element is not TextElement text)
                    continue;

                var isLabel = text.get_Parameter(BuiltInParameter.TAG_TAG) is not null;
                if (!isLabel)
                {
                    staticCount++;
                    if (staticTexts.Count < MaxStaticTextSamples)
                        staticTexts.Add(Truncate(text.Text ?? string.Empty, 60));
                    continue;
                }

                // Counted even beyond the cap: labelCount stays honest while
                // labels[] is truncated (labelsTruncated reports it).
                labelCount++;
                if (labels.Count >= MaxLabelsPerFamily)
                    continue;

                labels.Add(new Dictionary<string, object?>
                {
                    ["elementId"] = text.Id.GetValue(),
                    ["sampleText"] = Truncate(SampleText(text), 120)
                });
            }

            var categoryType = family.FamilyCategory?.CategoryType switch
            {
                CategoryType.Annotation => "annotation",
                CategoryType.Model => "model",
                null => null,
                _ => "other"
            };
            var payload = new Dictionary<string, object?>
            {
                ["familyId"] = family.Id.GetValue(),
                ["familyName"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                ["categoryType"] = categoryType,
                ["isAnnotationFamily"] = categoryType == "annotation",
                ["labelCount"] = labelCount,
                ["labelsTruncated"] = labelCount > labels.Count,
                ["labels"] = labels,
                ["staticTextCount"] = staticCount,
                ["staticTextsSample"] = staticTexts
            };
            var familyMessage =
                "Family '" + family.Name + "' (" + (family.FamilyCategory?.Name ?? "no category") +
                ", " + categoryType + "): " + labelCount + " label(s), " +
                staticCount + " static text(s). " +
                "The label→parameter link is not readable via the API before Revit 2027 " +
                "(CF-3799): re-binding a label is a manual Family Editor step.";
            return (payload, familyMessage, null);
        }
        finally
        {
            try
            {
                familyDoc!.Close(false);
            }
            catch (Exception)
            {
                // The read-only copy is discarded either way.
            }
        }
    }

    /// <summary>
    /// TAG_SAMPLE_TEXT carries the label's sample value; TextElement.Text is
    /// the fallback for builds that leave it empty.
    /// </summary>
    private static string? SampleText(TextElement text)
    {
        try
        {
            var sample = text.get_Parameter(BuiltInParameter.TAG_SAMPLE_TEXT)?.AsString();
            if (!string.IsNullOrEmpty(sample))
                return sample;
        }
        catch (Exception)
        {
            // Fall through to the Text property.
        }

        try
        {
            return text.Text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) || text!.Length <= max
            ? text ?? string.Empty
            : text.Substring(0, max) + "…";

    private static long? TryGetLong(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            return null;
        }

        return result;
    }

    private static IReadOnlyList<long>? TryGetIntArray(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<long>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var parsed))
                result.Add(parsed);
        }

        return result;
    }
}
