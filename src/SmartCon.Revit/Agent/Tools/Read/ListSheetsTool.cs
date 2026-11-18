using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class ListSheetsTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 500;

    public ListSheetsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_sheets",
        Description =
            "Lists sheets (листы) without opening them. Filters: number substring, name substring, " +
            "excludePlaceholders. Returns sheet id, number, name, size in millimetres, placed view " +
            "count, placed schedule count and isPlaceholder. Paged: limit (default 200, max 500), " +
            "offset, returned, nextOffset, truncated. fields=[\"sheetId\",\"number\",\"name\"] " +
            "projects each card onto the listed properties; unknown names are reported in " +
            "ignoredFields with availableFields.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""numberContains"":{""type"":""string""},""nameContains"":{""type"":""string""},""excludePlaceholders"":{""type"":""boolean"",""default"":false},""fields"":{""type"":""array"",""description"":""Allowed: sheetId,id,number|sheetNumber,name,uniqueId,widthMm,heightMm,viewCount,scheduleCount,isPlaceholder. Returned keys match the requested names."",""items"":{""type"":""string""}},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var numberContains = AgentJson.GetString(args, "numberContains");
        var nameContains = AgentJson.GetString(args, "nameContains");
        var excludePlaceholders = AgentJson.GetBool(args, "excludePlaceholders", false);
        var fields = AgentJson.GetStringArray(args, "fields");
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));

        var scheduleCounts = AgentSheetReader.CountPlacedSchedules(doc);
        var matched = new List<SheetCard>();
        foreach (var sheet in AgentSheetReader.Collect(doc, excludePlaceholders))
        {
            ct.ThrowIfCancellationRequested();

            if (!Contains(sheet.SheetNumber, numberContains))
                continue;
            if (!Contains(sheet.Name, nameContains))
                continue;

            matched.Add(AgentSheetReader.CreateCard(sheet, scheduleCounts));
        }

        matched.Sort((left, right) =>
        {
            var byNumber = string.Compare(left.Number, right.Number, StringComparison.OrdinalIgnoreCase);
            return byNumber != 0
                ? byNumber
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        var total = matched.Count;
        var page = AgentPaging.Slice(matched, offset, limit);
        var hasMore = offset + page.Count < total;

        if (fields.Count > 0)
        {
            var projected = new List<JsonElement>(page.Count);
            var ignored = new List<string>();
            var substituted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in page)
            {
                projected.Add(AgentCardProjection.Apply(
                    AgentJson.SerializeCard(card), fields, ignored, substituted));
            }

            var available = page.Count > 0
                ? AgentCardProjection.AvailableFields(AgentJson.SerializeCard(page[0]))
                : Array.Empty<string>();

            var projectedPayload = new
            {
                total,
                offset,
                returned = projected.Count,
                nextOffset = hasMore ? offset + projected.Count : (int?)null,
                truncated = hasMore || offset > 0,
                ignoredFields = ignored.Count > 0 ? ignored : null,
                substitutedFields = substituted.Count > 0 ? substituted : null,
                availableFields = ignored.Count > 0 ? available : null,
                sheets = projected
            };

            return ToolResult.Ok(
                AgentJson.Serialize(projectedPayload),
                "Matched " + total + " sheet(s); returned " + projected.Count + "." + PageHint(hasMore));
        }

        var payload = new
        {
            total,
            offset,
            returned = page.Count,
            nextOffset = hasMore ? offset + page.Count : (int?)null,
            truncated = hasMore || offset > 0,
            sheets = page
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Matched " + total + " sheet(s); returned " + page.Count + "." + PageHint(hasMore));
    }

    private static string PageHint(bool hasMore) =>
        hasMore
            ? " The result is truncated — pass nextOffset as offset to read the next page."
            : string.Empty;

    private static bool Contains(string? value, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;
        if (string.IsNullOrEmpty(value))
            return false;

#if NET8_0_OR_GREATER
        return value.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return value.IndexOf(needle!, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }
}