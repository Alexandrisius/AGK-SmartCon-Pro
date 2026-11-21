using System.Text.Json;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Outcome of the destructive-tool name confirmation (S.5): the apply call
/// must repeat the exact name set of a FRESH dry run, so a model that changed
/// between the two calls is refused instead of deleting the wrong objects.
/// </summary>
public sealed class AgentDeleteConfirmResult
{
    public bool Ok { get; init; }

    /// <summary>confirm_names_required | confirm_names_mismatch; null when Ok.</summary>
    public string? ErrorCode { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>Sorted fresh target names — the set the apply must repeat.</summary>
    public IReadOnlyList<string> Expected { get; init; } = Array.Empty<string>();

    /// <summary>Names the caller actually passed (duplicates removed).</summary>
    public IReadOnlyList<string> Got { get; init; } = Array.Empty<string>();

    /// <summary>Fresh targets missing from confirmNames.</summary>
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();

    /// <summary>Confirmed names that no longer match a fresh target.</summary>
    public IReadOnlyList<string> Unexpected { get; init; } = Array.Empty<string>();

    public static AgentDeleteConfirmResult OkResult(IReadOnlyList<string> expected) =>
        new() { Ok = true, Expected = expected };
}

/// <summary>
/// Pure confirmation logic shared by every destructive delete tool: compare a
/// freshly computed list of target names with the confirmNames argument of the
/// apply call. Comparison is set-based (duplicates ignored), case-sensitive
/// (Ordinal) — Revit names that differ by case are different names.
/// </summary>
public static class AgentDeleteConfirm
{
    public const string RequiredCode = "confirm_names_required";

    public const string MismatchCode = "confirm_names_mismatch";

    public static AgentDeleteConfirmResult Verify(
        IReadOnlyList<string> freshNames,
        JsonElement? confirmNamesArg)
    {
        var expected = DistinctSorted(freshNames);

        if (confirmNamesArg is not { ValueKind: JsonValueKind.Array })
        {
            return Required(expected, "confirmNames is required when dryRun=false.");
        }

        var got = new List<string>();
        foreach (var item in confirmNamesArg.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    got.Add(text!);
            }
        }

        if (got.Count == 0)
        {
            return Required(
                expected,
                "confirmNames is required when dryRun=false; pass the names[] list of a fresh dry run.");
        }

        var gotSet = new HashSet<string>(got, StringComparer.Ordinal);
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);

        var missing = expectedSet
            .Where(name => !gotSet.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var unexpected = gotSet
            .Where(name => !expectedSet.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0 && unexpected.Length == 0)
            return AgentDeleteConfirmResult.OkResult(expected);

        var gotSorted = gotSet
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return new AgentDeleteConfirmResult
        {
            Ok = false,
            ErrorCode = MismatchCode,
            Message =
                "confirmNames does not match the current targets (" + missing.Length +
                " missing, " + unexpected.Length + " unexpected); the model changed since " +
                "the dry run. Repeat the dry run and pass its fresh names[].",
            Expected = expected,
            Got = gotSorted,
            Missing = missing,
            Unexpected = unexpected
        };
    }

    private static AgentDeleteConfirmResult Required(
        IReadOnlyList<string> expected,
        string message) =>
        new()
        {
            Ok = false,
            ErrorCode = RequiredCode,
            Message = message,
            Expected = expected
        };

    private static string[] DistinctSorted(IReadOnlyList<string> names)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!string.IsNullOrEmpty(name))
                set.Add(name);
        }

        var sorted = set.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }
}
