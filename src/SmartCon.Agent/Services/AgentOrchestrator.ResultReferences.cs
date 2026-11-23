using System.Text.Json;
using System.Text.Json.Nodes;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services;

public sealed partial class AgentOrchestrator
{
    /// <summary>
    /// A tool argument may point at a part of a stored tool result instead of
    /// repeating it: {"$result": "&lt;resultHandle&gt;", "path": "&lt;path inside
    /// the stored result&gt;"}. The reference is replaced by the stored value
    /// before the tool validates its arguments, so a large result never has to
    /// be paged into the context and retyped by the model.
    /// </summary>
    internal const string ResultReferenceKey = "$result";

    private const string ResultReferencePathKey = "path";

    internal static bool MayContainResultReference(string? argumentsJson) =>
        !string.IsNullOrEmpty(argumentsJson)
        && argumentsJson!.IndexOf("\"" + ResultReferenceKey + "\":", StringComparison.Ordinal) >= 0;

    private async Task<(JsonElement Arguments, ToolResult? Failure)> ResolveResultReferencesAsync(
        JsonElement args,
        CancellationToken ct)
    {
        var failures = new List<(string Code, string Message)>();
        var root = JsonNode.Parse(args.GetRawText());
        var replaced = await VisitAsync(root, failures, ct).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            var first = failures[0];
            return (args, ToolResult.Fail(
                first.Code,
                "An argument reference cannot be resolved: " + first.Message +
                ". [Action: call read_tool_result with the handle and the path to see what is " +
                "stored there, or pass the value directly]"));
        }

        var resolved = (replaced ?? root)!.Deserialize<JsonElement>();
        return (resolved, null);
    }

    /// <summary>
    /// Walks the argument tree and returns the replacement of a reference
    /// node, or null when the node keeps its value.
    /// </summary>
    private async Task<JsonNode?> VisitAsync(
        JsonNode? node,
        List<(string Code, string Message)> failures,
        CancellationToken ct)
    {
        if (node is JsonObject reference && TryReadReference(reference, out var rootHandle, out var rootPath))
        {
            return await ResolveReferenceAsync(rootHandle!, rootPath!, failures, ct).ConfigureAwait(false);
        }

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(property => property.Key).ToArray())
            {
                if (obj[key] is JsonObject property && TryReadReference(property, out var handle, out var path))
                {
                    // A reference to a stored null is a value too: the property
                    // becomes null instead of keeping the reference object.
                    obj[key] = await ResolveReferenceAsync(handle!, path!, failures, ct).ConfigureAwait(false);
                    continue;
                }

                var child = await VisitAsync(obj[key], failures, ct).ConfigureAwait(false);
                if (child is not null)
                    obj[key] = child;
            }

            return null;
        }

        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonObject element && TryReadReference(element, out var handle, out var path))
                {
                    var value = await ResolveReferenceAsync(handle!, path!, failures, ct).ConfigureAwait(false);
                    if (value is JsonArray inner)
                    {
                        // Written as the only element of an array argument -
                        // items: [{"$result": …}] - the reference resolves to
                        // that very array: splice it in, not one level deeper.
                        // Nodes move out of their parent; a node with a parent
                        // cannot be inserted elsewhere.
                        array.RemoveAt(index);
                        while (inner.Count > 0)
                        {
                            var item = inner[0];
                            inner.RemoveAt(0);
                            array.Insert(index, item);
                            index++;
                        }

                        index--;
                        continue;
                    }

                    array[index] = value;
                    continue;
                }

                var child = await VisitAsync(array[index], failures, ct).ConfigureAwait(false);
                if (child is not null)
                    array[index] = child;
            }
        }

        return null;
    }

    private static bool TryReadReference(JsonObject obj, out string? handle, out string? path)
    {
        handle = null;
        path = null;
        if (obj.Count != 2
            || obj[ResultReferenceKey] is not JsonValue handleValue
            || !handleValue.TryGetValue<string>(out var handleText)
            || string.IsNullOrWhiteSpace(handleText)
            || obj[ResultReferencePathKey] is not JsonValue pathValue
            || !pathValue.TryGetValue<string>(out var pathText)
            || string.IsNullOrWhiteSpace(pathText))
        {
            return false;
        }

        handle = handleText;
        path = pathText;
        return true;
    }

    private async Task<JsonNode?> ResolveReferenceAsync(
        string handle,
        string path,
        List<(string Code, string Message)> failures,
        CancellationToken ct)
    {
        if (_toolResultBlobStore is null || _sessionContext.ConversationId == Guid.Empty)
        {
            failures.Add((
                "result_not_found",
                "stored tool results are not available in this session"));
            return null;
        }

        AgentToolResultBlob? blob;
        try
        {
            blob = await _toolResultBlobStore
                .GetAsync(_sessionContext.ConversationId, handle, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent failed to read a stored tool result for an argument reference: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: the model is told to pass the value directly instead of the reference]");
            failures.Add((
                "tool_failed",
                "the stored result '" + handle + "' could not be read: " + ex.Message));
            return null;
        }

        if (blob is null)
        {
            failures.Add((
                "result_not_found",
                "no stored result for handle '" + handle + "' (only results that were trimmed " +
                "are stored, and they belong to the chat that produced them)"));
            return null;
        }

        // A clone: the node sits inside the parsed stored result and cannot join another tree.
        var value = AgentResultPath.Resolve(blob.ResultJson, path, out var pathError)?.DeepClone();
        if (pathError is not null)
        {
            failures.Add((
                "invalid_arguments",
                "path '" + path + "' of the stored result '" + handle + "': " + pathError));
            return null;
        }

        return value;
    }
}
