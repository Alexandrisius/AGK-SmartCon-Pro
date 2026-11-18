using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.IntegrationTests.Agent.Eval;

/// <summary>
/// Canonicalizes Revit ElementIds in recorded tool calls. Fixtures store
/// {kind, name, category, index} instead of raw ids; on replay the same objects
/// are looked up in the freshly seeded document, so recorded transcripts stay
/// valid across runs (ids in a new project are always different).
/// </summary>
internal static class EvalIdSupport
{
    public static void Collect(
        string? argumentsJson,
        Document document,
        IDictionary<string, EvalIdRef> map)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(argumentsJson!);
        }
        catch (JsonException)
        {
            return;
        }

        CollectNode(node, document, map);
    }

    public static string Remap(
        string? argumentsJson,
        IReadOnlyDictionary<string, EvalIdRef> idMap,
        Document document)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || idMap.Count == 0)
            return argumentsJson ?? "{}";

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(argumentsJson!);
        }
        catch (JsonException)
        {
            return argumentsJson!;
        }

        var changed = RemapNode(node, idMap, document);
        return changed is null ? argumentsJson! : changed.ToJsonString();
    }

    private static void CollectNode(
        JsonNode? node,
        Document document,
        IDictionary<string, EvalIdRef> map)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                    CollectNode(pair.Value, document, map);
                break;

            case JsonArray array:
                foreach (var item in array)
                    CollectNode(item, document, map);
                break;

            case JsonValue value:
                if (value.TryGetValue<long>(out var id) && id > 0)
                {
                    var reference = Describe(document, id);
                    if (reference is not null)
                        map[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                            reference;
                }

                break;
        }
    }

    private static JsonNode? RemapNode(
        JsonNode? node,
        IReadOnlyDictionary<string, EvalIdRef> idMap,
        Document document)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var pair in obj)
                    result[pair.Key] = RemapNode(pair.Value, idMap, document)
                                       ?? JsonValue.Create((string?)null);
                return result;
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                    result.Add(RemapNode(item, idMap, document)
                               ?? JsonValue.Create((string?)null));
                return result;
            }

            case JsonValue value:
                if (value.TryGetValue<long>(out var id)
                    && idMap.TryGetValue(
                        id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        out var reference))
                {
                    var resolved = Resolve(document, reference);
                    if (resolved.HasValue)
                        return JsonValue.Create(resolved.Value);
                }

                return value.DeepClone();
        }

        return node?.DeepClone();
    }

    private static EvalIdRef? Describe(Document document, long id)
    {
        var element = document.GetElement(ElementIdCompat.Create(id));
        if (element is null)
            return null;

        if (element is View view)
        {
            var siblings = ViewsByName(document, view.Name);
            return new EvalIdRef
            {
                Kind = "view",
                Name = view.Name,
                Index = siblings.FindIndex(candidate => candidate.Id.GetValue() == id)
            };
        }

        if (element is Level level)
        {
            return new EvalIdRef { Kind = "level", Name = level.Name };
        }

        var name = element.Name;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var category = element.Category?.Name;
        var peers = ElementsByName(document, name!, category);
        return new EvalIdRef
        {
            Kind = "element",
            Name = name!,
            Category = category,
            Index = peers.FindIndex(candidate => candidate.Id.GetValue() == id)
        };
    }

    private static long? Resolve(Document document, EvalIdRef reference)
    {
        switch (reference.Kind)
        {
            case "view":
            {
                var views = ViewsByName(document, reference.Name);
                var element = At(views, reference.Index);
                return element?.Id.GetValue();
            }

            case "level":
            {
                var level = new FilteredElementCollector(document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, reference.Name, StringComparison.Ordinal));
                return level?.Id.GetValue();
            }

            case "element":
            {
                var elements = ElementsByName(document, reference.Name, reference.Category);
                var element = At(elements, reference.Index);
                return element?.Id.GetValue();
            }

            default:
                return null;
        }
    }

    private static List<Element> ViewsByName(Document document, string name) =>
        new FilteredElementCollector(document)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => string.Equals(view.Name, name, StringComparison.Ordinal))
            .OrderBy(view => view.Id.GetValue())
            .Cast<Element>()
            .ToList();

    private static List<Element> ElementsByName(
        Document document,
        string name,
        string? category)
    {
        return new FilteredElementCollector(document)
            .WhereElementIsNotElementType()
            .Where(element =>
                string.Equals(element.Name, name, StringComparison.Ordinal)
                && string.Equals(element.Category?.Name, category, StringComparison.Ordinal))
            .OrderBy(element => element.Id.GetValue())
            .ToList();
    }

    private static Element? At(IReadOnlyList<Element> elements, int index) =>
        index >= 0 && index < elements.Count ? elements[index] : elements.FirstOrDefault();
}
