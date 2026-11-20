using System.Text.Json;

namespace SmartCon.Core.Models.Agent.Jobs;

/// <summary>
/// Strict key validation for an action-specific spec. System.Text.Json silently
/// ignores unknown properties, which turns a typo such as <c>select.type</c> into
/// a different (and valid-looking) selection. Every action declares its allowed
/// keys and rejects the rest before any Revit work happens.
/// </summary>
public static class JobSpecKeyValidator
{
    /// <summary>
    /// Validates the spec against the declared object shapes. Paths use dot
    /// notation; a segment ending with <c>[]</c> iterates an array of objects
    /// (for example <c>mappings[].source</c>). An empty path validates the root.
    /// Returns null when every key is known.
    /// </summary>
    public static string? Validate(string? specJson, params JobSpecObjectRule[] rules)
    {
        if (string.IsNullOrWhiteSpace(specJson) || rules is null || rules.Length == 0)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(specJson!);
        }
        catch (JsonException)
        {
            // The deserializer reports malformed JSON with a better message.
            return null;
        }

        using (document)
        {
            var unknown = new List<string>();
            var allowed = new List<string>();

            foreach (var rule in rules)
            {
                allowed.Add(FormatRule(rule));
                foreach (var element in Resolve(document.RootElement, rule.Path))
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        if (rule.AllowedKeys.Any(key =>
                                string.Equals(key, property.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var path = string.IsNullOrEmpty(rule.Path)
                            ? property.Name
                            : rule.Path + "." + property.Name;
                        if (!unknown.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            unknown.Add(path);
                        }
                    }
                }
            }

            if (unknown.Count == 0)
            {
                return null;
            }

            return "unknown key(s): " + string.Join(", ", unknown) +
                   ". Allowed keys — " + string.Join("; ", allowed);
        }
    }

    private static string FormatRule(JobSpecObjectRule rule)
    {
        var name = string.IsNullOrEmpty(rule.Path) ? "spec" : rule.Path;
        return name + " {" + string.Join(", ", rule.AllowedKeys) + "}";
    }

    private static IEnumerable<JsonElement> Resolve(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield return root;
            yield break;
        }

        var current = new List<JsonElement> { root };
        foreach (var segment in path.Split('.'))
        {
            var isArray = segment.EndsWith("[]", StringComparison.Ordinal);
            var name = isArray ? segment.Substring(0, segment.Length - 2) : segment;
            var next = new List<JsonElement>();

            foreach (var element in current)
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetProperty(element, name, out var property))
                {
                    continue;
                }

                if (!isArray)
                {
                    if (property.ValueKind == JsonValueKind.Object)
                    {
                        next.Add(property);
                    }

                    continue;
                }

                if (property.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        next.Add(item);
                    }
                }
            }

            current = next;
            if (current.Count == 0)
            {
                yield break;
            }
        }

        foreach (var element in current)
        {
            yield return element;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed class JobSpecObjectRule
{
    public required string Path { get; init; }

    public required IReadOnlyList<string> AllowedKeys { get; init; }
}