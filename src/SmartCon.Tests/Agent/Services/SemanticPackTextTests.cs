using System.Text.RegularExpressions;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Text rules of the semantic pack (owner's harness feedback): the skill and the
/// cards describe the capability through abstractions — no benchmark cases, no
/// seed names — and the classifier codes come from a source of truth (the table
/// loaded in the document or a named file), never from a mandatory file path.
/// </summary>
public sealed class SemanticPackTextTests
{
    // Spoilers of the harness feedback: material lists, seed family names, line
    // counts and the "case" wording of the benchmark.
    private static readonly string[] BenchmarkSpoilers =
    {
        "кейс", "сталь", "медь", "ПВХ", "Кран шаровый", "медной трубы",
        "светильник", "120 строк", "100-200"
    };

    private static string CardText(string toolName)
    {
        Assert.True(AgentToolCards.TryGet(toolName, out var card), toolName + " has no card");
        return string.Join(
            "\n",
            new[] { card!.Purpose, card.WhenToUse, card.WhenNotToUse }
                .Concat(card.Examples)
                .Concat(card.Synonyms)
                .Concat(card.NegativeExamples));
    }

    [Fact]
    public void SkillAndCards_CarryNoBenchmarkSpecifics()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["skill"] = AgentToolPackSkills.TryGet("semantic")!,
            ["semantic_match"] = CardText("semantic_match"),
            ["read_classifier"] = CardText("read_classifier"),
            ["list_classification_entries"] = CardText("list_classification_entries"),
            ["load_classification_table"] = CardText("load_classification_table"),
            ["classify_types"] = CardText("classify_types")
        };

        Assert.Equal(6, texts.Count(value => !string.IsNullOrEmpty(value.Value)));
        foreach (var (name, text) in texts)
        {
            foreach (var spoiler in BenchmarkSpoilers)
            {
                Assert.True(
                    text.IndexOf(spoiler, StringComparison.OrdinalIgnoreCase) < 0,
                    name + " contains the benchmark specific '" + spoiler + "'");
            }
        }
    }

    [Fact]
    public void Skill_TakesClassifierCodesFromASourceOfTruth_NotFromAMandatoryPath()
    {
        var skill = AgentToolPackSkills.TryGet("semantic")!;

        Assert.Contains("### Откуда брать коды классификации", skill, StringComparison.Ordinal);
        Assert.Contains("в проекте не загружен классификатор", skill, StringComparison.Ordinal);
        Assert.Contains("Не подбирай код по аналогии", skill, StringComparison.Ordinal);
        // The old rule knew only a file and left the in-document classifier unserved.
        Assert.DoesNotContain("путь называется пользователем", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Skill_ClassificationSection_ExplainsWritableTypesBudgetsAndParameterNotSupported()
    {
        var skill = AgentToolPackSkills.TryGet("semantic")!;

        // classify_types: only types that can store the code are selected, the
        // rest is counted, and the leftovers of a truncated call are named.
        Assert.Contains("selection.skippedNotWritable", skill, StringComparison.Ordinal);
        Assert.Contains("TRUNCATED", skill, StringComparison.Ordinal);
        Assert.Contains("notProcessed.familyNames", skill, StringComparison.Ordinal);
        // set_parameters: an element that cannot carry the parameter is not a value problem.
        Assert.Contains("parameter_not_supported", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Cards_ExposeTheNewClassificationFacts()
    {
        Assert.True(AgentToolCards.TryGet("classify_types", out var classifyTypes));
        Assert.Contains("skippedNotWritable", classifyTypes!.Purpose, StringComparison.Ordinal);
        Assert.Contains("TRUNCATED", classifyTypes.Purpose, StringComparison.Ordinal);
        Assert.Contains("notProcessed.familyNames", classifyTypes.Purpose, StringComparison.Ordinal);
        // The card addresses the cut families the way the tool and the skill do.
        Assert.Contains(
            "(адреса — familyIds, systemTypeIds)",
            classifyTypes.Purpose, StringComparison.Ordinal);

        Assert.True(AgentToolCards.TryGet("set_parameters", out var setParameters));
        Assert.Contains("parameter_not_supported", setParameters!.Purpose, StringComparison.Ordinal);

        // The details contract (parameterNames accepts BuiltInParameter enum ids
        // such as UNIFORMAT_CODE) moved from the always-on card into the elements
        // skill with the prompt-budget handoff — see PromptBudgetSkillHandoffTests.
        var elementsSkill = AgentToolPackSkills.TryGet("elements")!;
        Assert.Contains("UNIFORMAT_CODE", elementsSkill, StringComparison.Ordinal);
        Assert.Contains("enum-id `BuiltInParameter`", elementsSkill, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadClassifierCard_PointsToTheDocumentTableWhenTheClassifierIsLoadedThere()
    {
        Assert.True(AgentToolCards.TryGet("read_classifier", out var card));

        Assert.Contains("get_project_overview", card!.WhenToUse, StringComparison.Ordinal);
        Assert.Contains("загружен в документ", card.WhenToUse, StringComparison.Ordinal);
        Assert.Contains("list_classification_entries", card.WhenToUse, StringComparison.Ordinal);
        Assert.Contains("Покажи классификатор, загруженный в проект", card.NegativeExamples);
    }

    [Fact]
    public void SemanticCards_NameOnlyTheToolsThatExist()
    {
        // An invented snake_case name would route the model to a call that
        // cannot succeed.
        var readClassifier = NamedTools(CardText("read_classifier"));
        var semanticMatch = NamedTools(CardText("semantic_match"));

        // Sanity: the scan does see the names the cards are allowed to carry.
        Assert.Contains("get_project_overview", readClassifier);
        Assert.Contains("query_elements", semanticMatch);

        Assert.All(
            readClassifier,
            name => Assert.Contains(
                name,
                new[] { "get_project_overview", "semantic_match", "set_parameters", "list_classification_entries" }));
        Assert.All(semanticMatch, name => Assert.Contains(name, new[] { "query_elements", "list_views" }));
    }

    private static string[] NamedTools(string text) =>
        Regex.Matches(text, @"\b[a-z]+_[a-z_]+\b")
            .Select(match => match.Value)
            .Distinct()
            .ToArray();

    [Fact]
    public void SemanticMatchCard_IsDiscoverableByClassificationTerms()
    {
        string[] required =
        {
            "классификатор", "классификация", "код по классификатору", "подбери код",
            "присвой коды", "сопоставь списки", "classification", "assembly code",
            "best match"
        };

        Assert.True(AgentToolCards.TryGet("semantic_match", out var card));
        Assert.All(required, term => Assert.Contains(term, card!.Synonyms));
    }
}
