using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Microsoft.Data.Sqlite;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Offline runtime check of the budget mechanics (C7-03/C2-02, C2-12) on the
/// real production loop with a scripted <see cref="FakeAgentLlmClient"/>: with
/// a round budget of 5 the harness steers the model at the 4th round boundary,
/// keeps the turn alive, and when the budget is spent it does NOT drop the
/// model — it makes one final call WITHOUT tools and keeps the final answer,
/// while the verdict stays capped at PARTIAL. Usage is the sum of every
/// completed LLM call. No network, no live LLM.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private const string BudgetPlumbingCaseText = "Покажи уровни проекта и дай отчёт.";

    private const string BudgetPlumbingFinalText =
        "ИТОГ: уровни перечислены; что сделано — уровни; что не сделано — остальное. " +
        "Harness feedback: удобно; неудобно; невозможно; не хватило.";

    private const int BudgetPlumbingToolRounds = 5;

    private static AgentUsage BudgetPlumbingUsage(int index) => new()
    {
        PromptTokens = 100 * index,
        CompletionTokens = 10 * index,
        TotalTokens = 110 * index
    };

    [Test]
    public async Task Budget_SteeringThenFinalAnswerWithoutTools()
    {
        var resultsRoot = Path.Combine(
            Path.GetTempPath(), "smartcon-bench-budget-" + Guid.NewGuid().ToString("N"));
        var settingNames = new[]
        {
            "SMARTCON_BENCH_RESULTS_DIR", "SMARTCON_BENCH_KEEP_DB",
            "SMARTCON_BENCH_MAX_ROUNDS", "SMARTCON_BENCH_MAX_SECONDS"
        };
        var previous = settingNames.ToDictionary(
            name => name,
            name => Environment.GetEnvironmentVariable(name),
            StringComparer.Ordinal);
        Environment.SetEnvironmentVariable("SMARTCON_BENCH_RESULTS_DIR", resultsRoot);
        Environment.SetEnvironmentVariable("SMARTCON_BENCH_KEEP_DB", "1");
        // Steering fires at ceil(5 * 0.8) = 4; the time steering (45 s of the
        // 60 s floor) is out of reach of a run that takes seconds.
        Environment.SetEnvironmentVariable("SMARTCON_BENCH_MAX_ROUNDS",
            BudgetPlumbingToolRounds.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SMARTCON_BENCH_MAX_SECONDS", "60");

        try
        {
            OpenBudgetPlumbingDocument();

            var fake = new FakeAgentLlmClient("budget-fake");
            for (var index = 1; index <= BudgetPlumbingToolRounds; index++)
            {
                fake.EnqueueAssistant(
                    null,
                    new[]
                    {
                        new AgentToolCall
                        {
                            Id = "call-" + index.ToString(CultureInfo.InvariantCulture),
                            Name = "list_levels",
                            ArgumentsJson = "{}"
                        }
                    },
                    usage: BudgetPlumbingUsage(index));
            }

            fake.EnqueueAssistant(BudgetPlumbingFinalText, usage: BudgetPlumbingUsage(BudgetPlumbingToolRounds + 1));

            var run = RunProductionCase(
                0,
                "budget plumbing",
                BudgetPlumbingCaseText,
                new[] { "synthetic empty project" },
                (_, _, _) => new CaseVerdict(
                    true, 1.0, "plumbing: состояние модели признано эталонным", Array.Empty<string>()),
                () => "state: n/a",
                llmClient: fake);

            // The budget was spent — and the final answer survived.
            await Assert.That(run.StopReason).IsEqualTo("round_budget");
            await Assert.That(run.Error).IsNull();
            await Assert.That(run.Answer).IsEqualTo(BudgetPlumbingFinalText);
            await Assert.That(run.Rounds).IsEqualTo(BudgetPlumbingToolRounds);

            // Every LLM request is accounted for: 5 working rounds + the final call.
            await Assert.That(fake.Requests.Count).IsEqualTo(BudgetPlumbingToolRounds + 1);
            foreach (var request in fake.Requests.Take(BudgetPlumbingToolRounds))
                await Assert.That(request.Tools.Count).IsGreaterThan(0);
            await Assert.That(fake.Requests[^1].Tools.Count).IsEqualTo(0);

            // Usage = the sum of ALL completed calls, not only the returned final turn
            // (600 / 60): the cancelled turns count too.
            var expectedPrompt = Enumerable.Range(1, BudgetPlumbingToolRounds + 1).Sum(index => 100 * index);
            await Assert.That(run.PromptTokens).IsEqualTo(expectedPrompt);
            await Assert.That(run.CompletionTokens).IsEqualTo(expectedPrompt / 10);

            // The artifact carries the verdict cap, the steering fact and the answer.
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(run.JsonPath));
            var root = document.RootElement;
            await Assert.That(root.GetProperty("stopReason").GetString()).IsEqualTo("round_budget");
            await Assert.That(root.GetProperty("steeringReason").GetString()).IsEqualTo("rounds");
            await Assert.That(root.GetProperty("budgetFinalCall").GetBoolean()).IsTrue();
            await Assert.That(root.GetProperty("usageFromStore").GetBoolean()).IsTrue();
            await Assert.That(root.GetProperty("rounds").GetInt32()).IsEqualTo(BudgetPlumbingToolRounds);
            await Assert.That(root.GetProperty("promptTokens").GetInt32()).IsEqualTo(expectedPrompt);
            await Assert.That(root.GetProperty("answer").GetString()).IsEqualTo(BudgetPlumbingFinalText);
            await Assert.That(root.GetProperty("toolCalls").GetInt32()).IsEqualTo(BudgetPlumbingToolRounds);
            await Assert.That(root.GetProperty("verdict").GetProperty("label").GetString()).IsEqualTo("PARTIAL");
            await Assert.That(root.GetProperty("verdict").GetProperty("score").GetDouble()).IsEqualTo(0.5);
            await Assert.That(root.GetProperty("stateVerdict").GetProperty("label").GetString()).IsEqualTo("PASS");

            // Conversation: each new turn's user message is preceded by the
            // orchestrator's System <turn_context> (B1-6), not by the tool
            // results of the interrupted round; the final demand precedes
            // exactly one tool-less answer.
            var roles = await BudgetPlumbingReadRoles(BenchRunArtifactWriter.DatabasePathFor(run.JsonPath));
            var steeringIndex = roles.IndexOf("User:" + SteeringRoundsText);
            await Assert.That(steeringIndex).IsGreaterThan(0);
            await Assert.That(roles.Count(role => role == "User:" + SteeringRoundsText)).IsEqualTo(1);
            await Assert.That(roles[steeringIndex - 1].StartsWith("System:", StringComparison.Ordinal))
                .IsTrue();
            await Assert.That(roles[steeringIndex - 1].Contains("<turn_context>", StringComparison.Ordinal))
                .IsTrue();

            var finalIndex = roles.IndexOf("User:" + FinalAnswerDemandText);
            await Assert.That(finalIndex).IsGreaterThan(steeringIndex);
            await Assert.That(roles.Count(role => role == "User:" + FinalAnswerDemandText)).IsEqualTo(1);
            await Assert.That(roles[finalIndex - 1].StartsWith("System:", StringComparison.Ordinal))
                .IsTrue();
            await Assert.That(roles[finalIndex - 1].Contains("<turn_context>", StringComparison.Ordinal))
                .IsTrue();
            var tail = roles.Skip(finalIndex + 1).ToList();
            await Assert.That(tail.Count(role => role.StartsWith("Assistant:", StringComparison.Ordinal)))
                .IsEqualTo(1);
            await Assert.That(tail[^1]).IsEqualTo("Assistant:" + BudgetPlumbingFinalText);
        }
        finally
        {
            foreach (var name in settingNames)
                Environment.SetEnvironmentVariable(name, previous[name]);

            try
            {
                if (Directory.Exists(resultsRoot))
                    Directory.Delete(resultsRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    /// <summary>Пустой проект на синтетической базе (документ закроет [After(Test)]).</summary>
    private void OpenBudgetPlumbingDocument()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    /// <summary>«Role:content» всех сообщений единственного разговора базы (по rowid).</summary>
    private static async Task<List<string>> BudgetPlumbingReadRoles(string databasePath)
    {
        var rows = await BudgetPlumbingReadRows(databasePath);
        return rows.Select(row => row.Role + ":" + row.Content).ToList();
    }

    private sealed record BudgetPlumbingRow(string Role, string Content, int ToolCallCount);

    private static async Task<List<BudgetPlumbingRow>> BudgetPlumbingReadRows(string databasePath)
    {
        await Assert.That(File.Exists(databasePath)).IsTrue();
        var rows = new List<BudgetPlumbingRow>();
        using var connection = new SqliteConnection("Data Source=" + databasePath + ";Mode=ReadOnly");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role, content, tool_calls_json FROM messages ORDER BY rowid;";
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var toolCallsJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            var toolCallCount = 0;
            if (!string.IsNullOrWhiteSpace(toolCallsJson))
            {
                using var toolCalls = JsonDocument.Parse(toolCallsJson);
                toolCallCount = toolCalls.RootElement.GetArrayLength();
            }

            rows.Add(new BudgetPlumbingRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                toolCallCount));
        }

        return rows;
    }
}
