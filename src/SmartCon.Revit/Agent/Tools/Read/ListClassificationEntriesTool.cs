using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Classification;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Lists the classification table the document carries — the rows behind the
/// «Код по классификатору» picker — so a code is taken from the table that is
/// really loaded. A document without a table is reported as such: there is no
/// code to read, and none may be picked from memory or by analogy.
/// </summary>
public sealed class ListClassificationEntriesTool : AgentReadToolBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private const string TableNotLoadedMessage =
        "No classification table is loaded in this document, so there are no codes to read. " +
        "Tell the user that the classifier is not loaded (Revit: Manage > Classification " +
        "Settings; older versions: Manage > Additional Settings > Assembly Code). If the " +
        "user names a classifier file, load it with load_classification_table(path); never " +
        "pick codes from memory or by analogy.";

    private static readonly string[] AvailableFields =
    {
        "code", "description", "level", "parentCode", "breadcrumb", "categoryId", "isLeaf"
    };

    private static readonly string[] DefaultFields = { "code", "breadcrumb" };

    public ListClassificationEntriesTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "list_classification_entries",
        Description =
            "Lists the classification table LOADED IN THE DOCUMENT (the rows of the «Код по " +
            "классификатору» / Assembly Code picker) — the source of truth for a " +
            "classification code. No file path is needed. contains: a substring of the code, " +
            "the description or the breadcrumb path; parentCode: one branch; level: 1-5; " +
            "leafOnly (default true): only the leaf codes, the ones assignable to elements " +
            "(an empty contains-search over leaves automatically retries all levels and is " +
            "marked leafOnlyFallback with isLeaf per row). " +
            "Records are compact (code, breadcrumb); fields adds description, level, " +
            "parentCode, categoryId, isLeaf. classification_table_not_loaded means the " +
            "document has no classifier: tell the user instead of guessing a code.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""documentId"":{""type"":""string"",""description"":""Background model routing: the documentId returned by open_model_background; omit for the active document""},""contains"":{""type"":""string"",""description"":""Case-insensitive substring of the code, the description or the breadcrumb path""},""parentCode"":{""type"":""string"",""description"":""Only this branch of the tree; the branch root is included""},""level"":{""type"":""integer"",""minimum"":1,""maximum"":5,""description"":""Only rows of this tree level (1 is a top branch)""},""leafOnly"":{""type"":""boolean"",""default"":true,""description"":""true: only leaf codes — the ones assignable to elements, as in the Revit picker""},""fields"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Record fields to return. Allowed: code, description, level, parentCode, breadcrumb, categoryId, isLeaf. Default: code, breadcrumb. Unknown names return invalid_arguments""},""limit"":{""type"":""integer"",""default"":50,""maximum"":500},""offset"":{""type"":""integer"",""default"":0,""minimum"":0}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = AgentPaging.NormalizeLimit(
            AgentJson.GetInt(args, "limit", DefaultLimit),
            DefaultLimit,
            MaxLimit);
        var leafOnly = AgentJson.GetBool(args, "leafOnly", true);

        var selection = AgentListPayload.SelectFields(
            AvailableFields,
            AgentJson.GetStringArray(args, "fields"),
            DefaultFields);
        if (!selection.IsValid)
            return ToolResult.Fail("invalid_arguments", selection.Error!);

        int? level = null;
        if (TryGetInt(args, "level", out var requestedLevel))
        {
            if (requestedLevel < 1 || requestedLevel > AgentClassificationFile.MaxLevel)
                return ToolResult.Fail(
                    "invalid_arguments",
                    "level must be a number from 1 to " + AgentClassificationFile.MaxLevel +
                    ", got " + requestedLevel + ".");
            level = requestedLevel;
        }

        var table = AgentClassificationReader.Read(doc);
        if (!table.Loaded)
        {
            return ToolResult.Fail(
                AgentClassificationGuard.TableNotLoaded,
                TableNotLoadedMessage,
                data: AgentJson.Serialize(new { loaded = false }));
        }

        var contains = AgentJson.GetString(args, "contains");
        var parentCode = AgentJson.GetString(args, "parentCode");

        // One row more than the page tells whether a next page exists.
        var page = table.Search(contains, parentCode, level, leafOnly, limit + 1, offset);

        // A term can live in branch (non-leaf) descriptions while no leaf
        // matches: an empty leaf-only search falls back to all levels so the
        // agent sees the branch instead of "no match" (S.46, #531).
        var leafFallback = false;
        if (page.Count == 0 && !string.IsNullOrWhiteSpace(contains) && leafOnly)
        {
            page = table.Search(contains, parentCode, level, false, limit + 1, offset);
            leafFallback = page.Count > 0;
        }

        var truncated = page.Count > limit;
        var entries = new List<Dictionary<string, object?>>(System.Math.Min(page.Count, limit));
        foreach (var entry in page.Take(limit))
        {
            var record = Record(entry, selection.Fields);
            if (leafFallback && !record.ContainsKey("isLeaf"))
                record["isLeaf"] = entry.IsLeaf;
            entries.Add(record);
        }

        var payload = new Dictionary<string, object?>
        {
            ["loaded"] = true,
            ["source"] = table.Source,
            ["entriesTotal"] = table.Entries.Count,
            ["leafTotal"] = table.Entries.Count(entry => entry.IsLeaf),
            ["returned"] = entries.Count,
            ["offset"] = offset,
            ["nextOffset"] = truncated ? offset + limit : null,
            ["truncated"] = truncated,
            ["entries"] = entries
        };
        if (leafFallback)
        {
            payload["leafOnlyFallback"] = true;
            payload["hint"] = "No leaf code matches '" + contains!.Trim() +
                              "'; these rows match the term in branch (non-leaf) descriptions. " +
                              "A branch row is not assignable - open it with parentCode to see its leaves.";
        }
        AgentListPayload.AddFieldInfo(payload, selection.Fields, AvailableFields);

        var message = new System.Text.StringBuilder();
        message.Append("Classification table '").Append(table.Source).Append("': ")
            .Append(entries.Count).Append(" of ").Append(table.Entries.Count)
            .Append(table.Entries.Count == 1 ? " entry" : " entries");
        if (truncated)
            message.Append(", next offset ").Append(offset + limit);

        message.Append('.');
        if (truncated)
        {
            var ways = new List<string> { "parentCode (one branch)", "contains (a substring of the code or the description)" };
            if (!level.HasValue)
                ways.Add("level (1-" + AgentClassificationFile.MaxLevel + ")");
            var narrow = AgentListPayload.TruncatedHint(ways);
            message.Append(narrow);
            payload["hint"] = narrow.Trim();
        }
        else if (entries.Count == 0)
        {
            var hint = NoMatchHint(table, parentCode, contains, level, leafOnly);
            message.Append(' ').Append(hint);
            payload["hint"] = hint;
        }

        if (selection.IsDefault && entries.Count >= AgentListPayload.CompactHintThreshold)
        {
            message.Append(AgentListPayload.CompactFieldsHint(
                selection.Fields,
                " id=code and text=breadcrumb are ready-made items for semantic_match."));
        }

        return ToolResult.Ok(AgentJson.Serialize(payload), message.ToString());
    }

    private static string NoMatchHint(
        AgentClassificationTable table,
        string? parentCode,
        string? contains,
        int? level,
        bool leafOnly)
    {
        var hint = new System.Text.StringBuilder("No entry matches");
        if (!string.IsNullOrWhiteSpace(parentCode) && table.Find(parentCode) is null)
        {
            hint.Append(": '").Append(parentCode!.Trim())
                .Append("' is not a code of this table");
        }
        else
        {
            hint.Append(" the filters (");
            var filters = new List<string>();
            if (!string.IsNullOrWhiteSpace(contains))
                filters.Add("contains='" + contains!.Trim() + "'");
            if (!string.IsNullOrWhiteSpace(parentCode))
                filters.Add("parentCode='" + parentCode!.Trim() + "'");
            if (level.HasValue)
                filters.Add("level=" + level.Value);
            if (leafOnly)
                filters.Add("leafOnly=true");
            hint.Append(string.Join(", ", filters)).Append(')');
        }

        hint.Append(". List the top-level branches with level:1, leafOnly:false, then narrow " +
                    "with parentCode or contains.");
        return hint.ToString();
    }

    private static Dictionary<string, object?> Record(
        AgentClassificationEntry entry,
        IReadOnlyList<string> fields)
    {
        var record = new Dictionary<string, object?>(fields.Count);
        foreach (var field in fields)
        {
            switch (field)
            {
                case "code":
                    record["code"] = entry.Code;
                    break;
                case "description":
                    record["description"] = entry.Description;
                    break;
                case "level":
                    record["level"] = entry.Level;
                    break;
                case "parentCode" when !string.IsNullOrEmpty(entry.ParentCode):
                    record["parentCode"] = entry.ParentCode;
                    break;
                case "breadcrumb":
                    record["breadcrumb"] = entry.Breadcrumb;
                    break;
                case "categoryId" when entry.CategoryId.HasValue:
                    record["categoryId"] = entry.CategoryId.Value;
                    break;
                case "isLeaf":
                    record["isLeaf"] = entry.IsLeaf;
                    break;
            }
        }

        return record;
    }

    private static bool TryGetInt(JsonElement args, string property, out int value)
    {
        value = 0;
        return args.ValueKind == JsonValueKind.Object
               && args.TryGetProperty(property, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out value);
    }
}
