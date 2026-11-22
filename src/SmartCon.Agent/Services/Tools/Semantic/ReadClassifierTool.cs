using System.IO;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools.Semantic;

/// <summary>
/// Reads a hierarchical txt classifier next to the model (real-cases benchmark
/// v2, case 5) and returns structured rows instead of the raw text: coded lines
/// ("30.20.10 Краны шаровые") become items with breadcrumbs
/// "раздел › подраздел › элемент", ready for semantic_match (id=code,
/// text=breadcrumb). The raw file never enters the chat context - that is the
/// whole point of the tool. Read-only, no Revit API.
/// </summary>
public sealed class ReadClassifierTool : IAgentTool
{
    public const string ToolName = "read_classifier";

    private const int DefaultMaxLines = 2000;
    private const int MaxLinesCap = 50000;

    private const string UsageHint =
        "Do not pick codes by eye from this list: semantic_match with " +
        "classifierPath=<this file> (and optional codePrefix) embeds the classifier " +
        "itself — pass queries=[{id: element/type name, query: what it is}] and it " +
        "returns the closest code per query in one call (up to 20 queries per call).";

    public ReadClassifierTool()
    {
        Definition = BuildDefinition();
    }

    public AgentToolDefinition Definition { get; }

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(ToolResult.Fail(
                "invalid_arguments", "path is required and must be a string."));
        }

        var path = (pathElement.GetString() ?? string.Empty).Trim();
        if (path.Length == 0 || !Path.IsPathRooted(path))
        {
            return Task.FromResult(ToolResult.Fail(
                "invalid_arguments",
                "path must be an absolute file path, got '" + path + "'."));
        }

        string? encodingName = null;
        if (args.TryGetProperty("encoding", out var encodingElement))
        {
            if (encodingElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(ToolResult.Fail(
                    "invalid_arguments", "encoding must be a string like utf-8 or windows-1251."));
            }

            encodingName = encodingElement.GetString()?.Trim();
        }

        var maxLines = DefaultMaxLines;
        if (args.TryGetProperty("maxLines", out var maxLinesElement))
        {
            if (maxLinesElement.ValueKind != JsonValueKind.Number
                || !maxLinesElement.TryGetInt32(out var parsed)
                || parsed < 1)
            {
                return Task.FromResult(ToolResult.Fail(
                    "invalid_arguments", "maxLines must be a positive integer."));
            }

            maxLines = Math.Min(parsed, MaxLinesCap);
        }

        return Task.FromResult(Execute(path, encodingName, maxLines));
    }

    private ToolResult Execute(string path, string? encodingName, int maxLines)
    {
        if (!File.Exists(path))
        {
            return ToolResult.Fail(
                "file_not_found",
                "Classifier file not found: " + path + ".");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Fail(
                "file_read_failed",
                "Could not read '" + path + "': " + ex.Message + ".");
        }

        string text;
        try
        {
            text = DecodeText(bytes, encodingName);
        }
        catch (ArgumentException)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Unknown encoding '" + encodingName + "'. Use names like utf-8 or windows-1251.");
        }

        var result = ClassifierTextParser.Parse(text, maxLines);

        var warnings = new List<string>();
        if (result.Truncated)
        {
            warnings.Add(
                "The file has " + result.TotalLines + " lines; only the first " + maxLines +
                " (maxLines) were parsed. [Action: raise maxLines if the classifier is longer]");
        }

        if (result.Items.Count == 0)
        {
            warnings.Add(
                "No coded lines (like '30.20.10 Название') were recognised. " +
                "[Action: check the file format - codes must be two-digit dot separated segments]");
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            items = result.Items.Select(item => new
            {
                code = item.Code,
                breadcrumb = item.Breadcrumb,
                level = item.Level,
                name = item.Name,
                line = item.Line,
                children = item.Children.Count > 0 ? item.Children : null
            }).ToList(),
            total = result.Items.Count,
            truncated = result.Truncated,
            path,
            parsedLevels = result.ParsedLevels,
            skippedLines = result.SkippedLines,
            totalLines = result.TotalLines,
            hint = UsageHint
        });

        return ToolResult.Ok(
            payload,
            "Parsed " + result.Items.Count + " classifier entries (" +
            result.ParsedLevels.Count + " level(s)) from " + path + ". " + UsageHint,
            affectedCount: result.Items.Count,
            warnings: warnings.Count == 0 ? null : warnings);
    }

    /// <summary>
    /// UTF-8 first with a Windows-1251 fallback for legacy Russian files: a
    /// strict UTF-8 decode throws on the broken byte sequences a 1251 file is
    /// full of, which is exactly the detection signal. BOMs win outright.
    /// </summary>
    internal static string DecodeText(byte[] bytes, string? encodingName)
    {
        if (!string.IsNullOrWhiteSpace(encodingName))
            return Encoding.GetEncoding(encodingName).GetString(bytes);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // windows-1251 and other ANSI codepages are not built into .NET 8:
        // the CodePages provider must be registered before GetEncoding(1251).
        // net48 has every Windows codepage natively and no provider type.
#if NET8_0_OR_GREATER
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
#endif

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("windows-1251").GetString(bytes);
        }
    }

    /// <summary>
    /// Compact mode of semantic_match (#470): parse the classifier file and
    /// filter rows by code prefix without shipping the array through the
    /// model's tool arguments.
    /// </summary>
    internal static bool TryReadItems(
        string path,
        string? codePrefix,
        int maxItems,
        out List<(string Id, string Text)> items,
        out string? error)
    {
        items = new List<(string, string)>();
        error = null;

        string content;
        try
        {
            if (!File.Exists(path))
            {
                error = "file_not_found: " + path;
                return false;
            }

            content = DecodeText(File.ReadAllBytes(path), null);
        }
        catch (Exception ex)
        {
            error = "file_read_failed: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }

        var parsed = ClassifierTextParser.Parse(content, 50000);
        var prefix = codePrefix?.Trim();
        foreach (var item in parsed.Items)
        {
            if (items.Count >= maxItems)
                break;

            if (!string.IsNullOrEmpty(prefix)
                && (item.Code is null
                    || !item.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            items.Add((item.Code ?? item.Line.ToString(), item.Breadcrumb));
        }

        if (items.Count == 0)
            error = "no_rows_matched: the filter matched no classifier rows.";

        return items.Count > 0;
    }

    private static AgentToolDefinition BuildDefinition()
    {
        const string schema = @"{""type"":""object"",""properties"":{" +
            @"""path"":{""type"":""string"",""description"":""Absolute path to the classifier txt file""}," +
            @"""encoding"":{""type"":""string""," +
            @"""description"":""Optional encoding (utf-8, windows-1251); default: UTF-8 with automatic windows-1251 fallback""}," +
            @"""maxLines"":{""type"":""integer"",""minimum"":1,""maximum"":50000,""default"":2000," +
            @"""description"":""Parse at most this many lines; truncated=true warns when the file is longer""}}," +
            @"""required"":[""path""],""additionalProperties"":false}";

        return new AgentToolDefinition
        {
            Name = ToolName,
            Description =
                "Parses a hierarchical txt classifier (codes like 30.20.10 with Russian " +
                "names, tab or space indentation) into structured items: code, level, " +
                "name, line and the stitched breadcrumb 'раздел › подраздел › элемент'. " +
                "The array is ready for semantic_match (id=code, text=breadcrumb) - the " +
                "raw file text never enters the context, which is how a long " +
                "classifier stays cheap. Lines without a code on a deeper indent become " +
                "children of the previous coded line. Comments (# or //) and empty " +
                "lines are skipped; encoding is auto-detected (UTF-8, else " +
                "windows-1251). Read-only.",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            Category = "Read",
            Pack = "semantic",
            TimeoutMs = 30000,
            InputSchemaJson = schema
        };
    }
}
