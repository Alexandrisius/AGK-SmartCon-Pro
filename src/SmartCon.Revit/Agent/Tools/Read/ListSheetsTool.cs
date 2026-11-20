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
            "count, placed schedule count (title-block revision schedules are excluded and " +
            "reported as revisionScheduleCount) and isPlaceholder. Number matching also covers the " +
            "visible glyphs: numberVisual/hasInvisibleCharacters/numberCodePoints expose numbers " +
            "that differ only by invisible code units, and duplicateVisualNumber marks visually " +
            "identical numbers. Paged: limit (default 200, max 500), offset, returned, nextOffset, " +
            "truncated. fields=[\"sheetId\",\"number\",\"name\"] projects each card onto the listed " +
            "properties; unknown names are reported in ignoredFields with availableFields.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""numberContains"":{""type"":""string""},""nameContains"":{""type"":""string""},""excludePlaceholders"":{""type"":""boolean"",""default"":false},""fields"":{""type"":""array"",""description"":""Allowed: sheetId,id,number|sheetNumber,name,uniqueId,numberVisual,hasInvisibleCharacters,numberCodePoints,duplicateVisualNumber,widthMm,heightMm,viewCount,scheduleCount,revisionScheduleCount,isPlaceholder. Returned keys match the requested names."",""items"":{""type"":""string""}},""limit"":{""type"":""integer"",""default"":200,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
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

        var sheets = AgentSheetReader.Collect(doc, excludePlaceholders);
        var visualDuplicates = AgentSheetReader.FindVisualDuplicateNumbers(sheets);
        var scheduleCounts = AgentSheetReader.CountPlacedSchedules(doc);
        var matched = new List<SheetCard>();
        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();

            if (!MatchesNumber(sheet.SheetNumber, numberContains))
                continue;
            if (!Contains(sheet.Name, nameContains))
                continue;

            matched.Add(AgentSheetReader.CreateCard(sheet, scheduleCounts, visualDuplicates));
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
        var duplicates = DescribeVisualDuplicates(matched);

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
                visualDuplicates = duplicates,
                sheets = projected
            };

            return ToolResult.Ok(
                AgentJson.Serialize(projectedPayload),
                "Matched " + total + " sheet(s); returned " + projected.Count + "." +
                DuplicateHint(duplicates) + PageHint(hasMore));
        }

        var payload = new
        {
            total,
            offset,
            returned = page.Count,
            nextOffset = hasMore ? offset + page.Count : (int?)null,
            truncated = hasMore || offset > 0,
            visualDuplicates = duplicates,
            sheets = page
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Matched " + total + " sheet(s); returned " + page.Count + "." +
            DuplicateHint(duplicates) + PageHint(hasMore));
    }

    private static List<VisualDuplicateGroup>? DescribeVisualDuplicates(
        IReadOnlyList<SheetCard> cards)
    {
        var groups = new Dictionary<string, List<SheetCard>>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            if (!card.DuplicateVisualNumber)
                continue;

            if (!groups.TryGetValue(card.NumberVisual, out var group))
            {
                group = new List<SheetCard>();
                groups[card.NumberVisual] = group;
            }

            group.Add(card);
        }

        if (groups.Count == 0)
            return null;

        var result = new List<VisualDuplicateGroup>(groups.Count);
        foreach (var pair in groups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new VisualDuplicateGroup
            {
                VisualNumber = pair.Key,
                Count = pair.Value.Count,
                Sheets = pair.Value.Select(card => new VisualDuplicateSheet
                {
                    SheetId = card.SheetId,
                    Number = card.Number,
                    NumberCodePoints = card.NumberCodePoints,
                    HasInvisibleCharacters = card.HasInvisibleCharacters,
                    Name = card.Name
                }).ToList()
            });
        }

        return result;
    }

    private static string DuplicateHint(IReadOnlyList<VisualDuplicateGroup>? duplicates)
    {
        if (duplicates is null || duplicates.Count == 0)
            return string.Empty;

        var sheets = duplicates.Sum(group => group.Count);
        return " Warning: " + sheets + " sheet(s) in " + duplicates.Count +
               " group(s) have visually identical numbers; identify them by sheetId.";
    }

    private sealed class VisualDuplicateGroup
    {
        public string VisualNumber { get; set; } = string.Empty;

        public int Count { get; set; }

        public List<VisualDuplicateSheet> Sheets { get; set; } = new();
    }

    private sealed class VisualDuplicateSheet
    {
        public long SheetId { get; set; }

        public string Number { get; set; } = string.Empty;

        public IReadOnlyList<int>? NumberCodePoints { get; set; }

        public bool HasInvisibleCharacters { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private static string PageHint(bool hasMore) =>
        hasMore
            ? " The result is truncated — pass nextOffset as offset to read the next page."
            : string.Empty;

    private static bool MatchesNumber(string? value, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;

        if (Contains(value, needle))
            return true;

        return Contains(AgentTextAudit.Visual(value), AgentTextAudit.Visual(needle));
    }

    private static bool Contains(string? value, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;
        if (string.IsNullOrEmpty(value))
            return false;

#if NET8_0_OR_GREATER
        return value.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return value!.IndexOf(needle!, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }
}