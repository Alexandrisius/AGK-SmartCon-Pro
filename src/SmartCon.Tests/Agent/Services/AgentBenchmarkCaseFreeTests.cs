using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The agent must generalize, not replay the benchmark. Cards and pack skills
/// may show examples, but never a name, query or artifact that appears in
/// <c>docs/agent/real-cases-benchmark-v2.md</c> — otherwise the ten graded tasks
/// leak into the runtime text the model reads. The markers below are the
/// distinctive tokens of those tasks (owner directives 2026-11-24:
/// "ни один кейс не был захардкожен", "примеры — но точно не из нашего набора").
/// </summary>
public sealed class AgentBenchmarkCaseFreeTests
{
    private static readonly string[] CaseMarkers =
    {
        "адск", "adsk_", "тдск", "в_этаж", "в_пэ", "т11", "т111", "т1.11",
        "офис 1", "кратност", "ведомость объёмов", "план 1 этажа", "план 2 этажа",
        "30.20.10", "гф-021", "пф-115"
    };

    [Fact]
    public void CardsSkillsAndStaticPrompt_CarryNoBenchmarkCaseMarkers()
    {
        var corpus = new List<(string Where, string Text)>();
        foreach (var card in AgentToolCards.All)
        {
            corpus.Add(($"card:{card.ToolName}", string.Join("\n",
                new[] { card.Purpose, card.WhenToUse, card.WhenNotToUse }
                    .Concat(card.Examples)
                    .Concat(card.Synonyms)
                    .Concat(card.NegativeExamples))));
        }

        foreach (var pack in AgentToolPackSkills.PacksWithSkill)
            corpus.Add(($"skill:{pack}", AgentToolPackSkills.TryGet(pack) ?? string.Empty));

        corpus.Add(("system-prompt", SmartCon.Core.Models.Agent.AgentSystemPrompt.BuildStatic()));

        var violations = FindMarkers(corpus);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void AgentSourceTree_CarriesNoBenchmarkCaseMarkers()
    {
        var root = FindRepositoryRoot();
        var directories = new[]
        {
            Path.Combine("src", "SmartCon.Core", "Services", "Agent"),
            Path.Combine("src", "SmartCon.Core", "Models", "Agent"),
            Path.Combine("src", "SmartCon.Agent", "Services"),
            Path.Combine("src", "SmartCon.Revit", "Agent")
        };

        var corpus = new List<(string Where, string Text)>();
        foreach (var directory in directories)
        {
            var full = Path.Combine(root, directory);
            if (!Directory.Exists(full))
                continue;
            foreach (var file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                corpus.Add((Path.GetRelativePath(root, file), File.ReadAllText(file)));
            }
        }

        Assert.True(corpus.Count > 50, $"Agent sources were not found under {root}.");
        var violations = FindMarkers(corpus);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private static List<string> FindMarkers(IEnumerable<(string Where, string Text)> corpus)
    {
        var violations = new List<string>();
        foreach (var (where, text) in corpus)
        {
            var lower = text.ToLowerInvariant();
            foreach (var marker in CaseMarkers)
            {
                var at = lower.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                var line = lower.Take(at).Count(ch => ch == '\n') + 1;
                violations.Add($"{where}:{line}: benchmark case marker \"{marker}\"");
            }
        }

        return violations;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "SmartCon.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root with src{Path.DirectorySeparatorChar}SmartCon.sln not found above {AppContext.BaseDirectory}.");
    }
}