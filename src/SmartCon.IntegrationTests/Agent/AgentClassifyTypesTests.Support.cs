using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Classification;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Helpers and the deterministic decision client of
/// <see cref="AgentClassifyTypesTests"/>: the tool's selection and writability
/// predicates recomputed here (the private originals are not callable) and the
/// set_parameters plumbing the tests share. Partial of the test class.
/// </summary>
public sealed partial class AgentClassifyTypesTests
{
    private static JsonElement Args(object value)
    {
        using var document = JsonSerializer.SerializeToDocument(value);
        return document.RootElement.Clone();
    }

    private ToolResult LoadSampleTable(string fileName = "classify-sample.txt")
    {
        var path = ClassificationFixtures.WriteTable(
            fileName, ClassificationFixtures.SampleTable, ClassificationFixtures.Utf16);
        return new LoadClassificationTableTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(Args(new { path, dryRun = false }), approved: true, CancellationToken.None);
    }

    private Task<ToolResult> ClassifyAsync(
        object args, KeywordDecisionClient? client = null) =>
        new ClassifyTypesTool(
                new StubRevitContext(Doc),
                new AgentSemanticRanker(client ?? new KeywordDecisionClient()))
            .ExecuteForTestsAsync(Doc, Args(args), CancellationToken.None);

    private ToolResult Set(JsonElement items, bool dryRun) =>
        new SetParametersTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Args(new { items, dryRun }),
                approved: true,
                CancellationToken.None);

    private string? CodeOf(long typeId)
    {
        var parameter = Doc.GetElement(ElementIdCompat.Create(typeId))?
            .get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn);
        if (parameter is null || !parameter.HasValue)
            return null;

        var code = parameter.AsString();
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    private string? DescriptionOf(long typeId) =>
        Doc.GetElement(ElementIdCompat.Create(typeId))?
            .get_Parameter(AgentParameterResolver.AssemblyDescriptionBuiltIn)?
            .AsString();

    /// <summary>
    /// data.readBack of a set_parameters result: every changed item verified,
    /// nothing mismatched or unverifiable (classification codes are plain text).
    /// </summary>
    private static async Task AssertReadBackMatchesChanged(ToolResult result)
    {
        var readBack = result.Data!.Value.GetProperty("readBack");
        var changed = result.Data.Value.GetProperty("changed").GetArrayLength();
        await Assert.That(readBack.GetProperty("expected").GetInt32()).IsEqualTo(changed);
        await Assert.That(readBack.GetProperty("verified").GetInt32()).IsEqualTo(changed);
        await Assert.That(readBack.GetProperty("mismatched").GetInt32()).IsEqualTo(0);
        await Assert.That(readBack.GetProperty("notVerifiable").GetInt32()).IsEqualTo(0);
    }

    /// <summary>
    /// Family -> type count of one classify result: assignments (typeIds) plus
    /// uncertain groups (typeIds) cover every selected family of a fresh
    /// document (nothing is already_set).
    /// </summary>
    private static Dictionary<string, int> FamilyTypeCounts(JsonElement data)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in data.GetProperty("assignments").EnumerateArray())
        {
            counts[assignment.GetProperty("family").GetString()!] =
                assignment.GetProperty("typeIds").GetArrayLength();
        }

        foreach (var group in data.GetProperty("uncertain").EnumerateArray())
        {
            counts[group.GetProperty("family").GetString()!] =
                group.GetProperty("typeIds").GetArrayLength();
        }

        return counts;
    }

    /// <summary>
    /// The wall families as the tool orders them: candidates are sorted by family
    /// name, groups take the order of their first type - (family, type count).
    /// </summary>
    private List<(string Family, int Count)> WallFamiliesInToolOrder() =>
        _wallTypeIds
            .Select(id => (WallType)Doc.GetElement(ElementIdCompat.Create(id))!)
            .GroupBy(type => type.FamilyName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Family: group.Key, Count: group.Count()))
            .OrderBy(family => family.Family, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The tool's writability rule, computed here (the private
    /// SupportsAssemblyCode is not callable): the parameter object is handed to
    /// almost every type, the value lives only on the types that list it among
    /// their own parameters.
    /// </summary>
    private static bool StoresAssemblyCode(ElementType type, Parameter codeParameter)
    {
        foreach (Parameter parameter in type.Parameters)
        {
            if (parameter.Id == codeParameter.Id)
                return true;
        }

        return false;
    }

    /// <summary>
    /// First type of the fresh template whose Assembly Code parameter object
    /// exists but stores no value (view family types, dimension styles and the
    /// like). <paramref name="requireWritablePath"/> also demands a non-readonly
    /// parameter so a write reaches the Set-refusal branch, not readonly_parameter.
    /// </summary>
    private static ElementType? FindTypeThatCannotStoreTheCode(Document doc, bool requireWritablePath)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ElementType))
            .Cast<ElementType>()
            .FirstOrDefault(type =>
            {
                var codeParameter = type.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn);
                if (codeParameter is null)
                    return false;
                if (requireWritablePath && codeParameter.IsReadOnly)
                    return false;
                return !StoresAssemblyCode(type, codeParameter);
            });
    }

    /// <summary>
    /// A classify_types filter (category of the type, else its name) whose whole
    /// matching set cannot store the code - verified against the document with
    /// the tool's own predicates, not assumed. null when no such filter exists.
    /// </summary>
    private static object? FilterSelectingOnlyUnwritableTypes(Document doc, ElementType type)
    {
        if (type.Category is not null
            && CategoryByName(doc, type.Category.Name) is { } category)
        {
            if (TypesMatching(doc, category.Id.GetValue(), null)
                .All(candidate => !StoresAssemblyCode(
                    candidate,
                    candidate.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn)!)))
            {
                return new { categories = new[] { type.Category.Name } };
            }
        }

        return TypesMatching(doc, null, type.Name)
            .All(candidate => !StoresAssemblyCode(
                candidate,
                candidate.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn)!))
            ? new { nameContains = type.Name }
            : null;
    }

    private static Category? CategoryByName(Document doc, string name) =>
        doc.Settings.Categories.Cast<Category>()
            .FirstOrDefault(category => string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tool's selection predicates: category id and nameContains.</summary>
    private static List<ElementType> TypesMatching(Document doc, long? categoryId, string? nameContains)
    {
        var matches = new List<ElementType>();
        foreach (var type in new FilteredElementCollector(doc)
                     .OfClass(typeof(ElementType))
                     .Cast<ElementType>())
        {
            var codeParameter = type.get_Parameter(AgentParameterResolver.AssemblyCodeBuiltIn);
            if (codeParameter is null)
                continue;

            if (categoryId.HasValue
                && (type.Category is null || type.Category.Id.GetValue() != categoryId.Value))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(nameContains)
                && System.Math.Max(
                        (type.FamilyName ?? string.Empty).IndexOf(nameContains, StringComparison.OrdinalIgnoreCase),
                        type.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                continue;
            }

            matches.Add(type);
        }

        return matches;
    }

    /// <summary>
    /// Deterministic stand-in for the decision model: every option is scored by
    /// the tokens it shares with the question's Match line; without any overlap
    /// the first option by code order wins (the plumbing, not the model
    /// quality, is what these tests verify).
    /// </summary>
    private sealed class KeywordDecisionClient : IAgentDecisionClient
    {
        public double Score { get; set; } = 0.9;

        public bool Enabled { get; set; } = true;

        public string ProviderName => "test";

        public string Model => "test-model";

        public bool IsEnabled => Enabled;

        public Task<IReadOnlyDictionary<string, AgentDecisionAnswer>> AskAsync(
            string state,
            IReadOnlyDictionary<string, AgentDecisionQuestion> questions,
            CancellationToken ct)
        {
            var answers = new Dictionary<string, AgentDecisionAnswer>(StringComparer.Ordinal);
            foreach (var pair in questions)
            {
                var marker = pair.Value.Instructions.IndexOf("Match: ", StringComparison.Ordinal);
                var query = marker >= 0
                    ? pair.Value.Instructions.Substring(marker + "Match: ".Length)
                    : pair.Value.Instructions;
                var queryTokens = Tokens(query);
                var ranked = pair.Value.Criteria!
                    .Select(option => (
                        Id: option.Key,
                        Overlap: Tokens(option.Value ?? string.Empty)
                            .Count(token => queryTokens.Any(q => Matches(q, token)))))
                    .OrderByDescending(option => option.Overlap)
                    .ThenBy(option => option.Id, StringComparer.Ordinal)
                    .ToList();

                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                var rank = 0;
                foreach (var option in ranked.Where(option => option.Overlap > 0).Take(5))
                {
                    probabilities[option.Id] = System.Math.Max(0.05, Score - rank * 0.1);
                    rank++;
                }

                if (probabilities.Count == 0)
                    probabilities[ranked[0].Id] = Score;

                answers[pair.Key] = new AgentDecisionAnswer
                {
                    Type = "choice",
                    Probabilities = probabilities
                };
            }

            return Task.FromResult<IReadOnlyDictionary<string, AgentDecisionAnswer>>(answers);
        }

        private static HashSet<string> Tokens(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            var current = new System.Text.StringBuilder();
            foreach (var character in text)
            {
                if (char.IsLetterOrDigit(character))
                {
                    current.Append(character);
                    continue;
                }

                Flush(current, tokens);
            }

            Flush(current, tokens);
            return tokens;
        }

        private static void Flush(System.Text.StringBuilder builder, HashSet<string> tokens)
        {
            if (builder.Length > 0)
                tokens.Add(builder.ToString().ToLowerInvariant());
            builder.Clear();
        }

        private static bool Matches(string left, string right) =>
            string.Equals(left, right, StringComparison.Ordinal)
            || (System.Math.Min(left.Length, right.Length) >= 5
                && (left.StartsWith(right, StringComparison.Ordinal)
                    || right.StartsWith(left, StringComparison.Ordinal)));
    }
}
