using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Agent.Support;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Offline plumbing test of the production-loop benchmark harness: a scripted
/// <see cref="FakeAgentLlmClient"/> (injected through
/// <see cref="ProductionAgentHost.Create(Document, IAgentLlmClient)"/>) drives
/// one real tool call through the production orchestrator, and the benchmark
/// artifact writer must produce a JSON artifact and a transcript that carry the
/// rounds, the call and the verdict. No network, no live LLM.
/// </summary>
public sealed class AgentBenchHarnessPlumbingTests : RevitApiTest
{
    private Document? _document;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Close()
    {
        _document?.Close(false);
    }

    [Test]
    public async Task ProductionLoop_WritesArtifactAndTranscript()
    {
        var resultsRoot = Path.Combine(
            Path.GetTempPath(), "smartcon-bench-plumbing-" + Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("SMARTCON_BENCH_RESULTS_DIR");
        Environment.SetEnvironmentVariable("SMARTCON_BENCH_RESULTS_DIR", resultsRoot);
        try
        {
            // RunProductionCase-equivalent: production host + fake client + one
            // conversation + one turn with a single read tool call + final text.
            var fake = new FakeAgentLlmClient();
            fake.EnqueueAssistant(
                null,
                new[]
                {
                    new AgentToolCall { Id = "call-1", Name = "list_levels", ArgumentsJson = "{}" }
                });
            fake.EnqueueAssistant("Готово: уровни перечислены.");

            using var host = ProductionAgentHost.Create(Doc, fake);
            var conversationId = host.CreateConversation("plumbing");

            var result = await host.Orchestrator.RunTurnAsync(
                conversationId,
                "Покажи уровни проекта.",
                Array.Empty<AgentAttachment>(),
                progress: null,
                CancellationToken.None);

            var toolRecords = host.ReadToolCalls(conversationId);
            var messages = host.ReadConversation(conversationId);

            var artifact = new BenchRunArtifact
            {
                CaseNumber = 0,
                CaseKey = "plumbing",
                Title = "Plumbing",
                Base = BenchRunArtifactWriter.BaseLabel(),
                Provider = host.Settings.Provider,
                Model = fake.Model,
                Reasoning = host.Settings.ReasoningEffort + "/" + host.Settings.EffectiveReasoningFormat,
                MaxOutputTokens = host.Settings.MaxOutputTokens,
                MaxRounds = 48,
                MaxSeconds = 900,
                StartedUtc = DateTime.UtcNow.AddSeconds(-2),
                EndedUtc = DateTime.UtcNow,
                Rounds = messages.Count(message =>
                    message.Role == AgentRole.Assistant && message.ToolCalls.Count > 0),
                ToolCalls = toolRecords.Count,
                PromptTokens = result.Usage.PromptTokens,
                CompletionTokens = result.Usage.CompletionTokens,
                StopReason = "ok",
                Answer = result.FinalText,
                Verdict = new BenchVerdictArtifact
                {
                    Label = "PASS",
                    Score = 1.0,
                    Detail = "plumbing verdict"
                },
                Calls = toolRecords.Select((record, index) => new BenchCallArtifact
                {
                    Round = 1,
                    Seq = index + 1,
                    Tool = record.ToolName,
                    Args = record.ArgumentsJson ?? string.Empty,
                    Success = string.Equals(record.Status, "ok", StringComparison.Ordinal),
                    ErrorCode = record.Error,
                    DurationMs = record.DurationMs
                }).ToList()
            };

            var (jsonPath, markdownPath) = BenchRunArtifactWriter.Write(artifact, "# transcript");

            await Assert.That(File.Exists(jsonPath)).IsTrue();
            await Assert.That(File.Exists(markdownPath)).IsTrue();
            await Assert.That(jsonPath.Contains("plumbing", StringComparison.Ordinal)).IsTrue();

            var json = await File.ReadAllTextAsync(jsonPath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            await Assert.That(root.GetProperty("toolCalls").GetInt32()).IsEqualTo(toolRecords.Count);
            await Assert.That(root.GetProperty("maxSeconds").GetInt32()).IsEqualTo(900);
            await Assert.That(root.GetProperty("stopReason").GetString()).IsEqualTo("ok");
            await Assert.That(root.GetProperty("verdict").GetProperty("label").GetString())
                .IsEqualTo("PASS");
            await Assert.That(root.GetProperty("calls").GetArrayLength())
                .IsEqualTo(toolRecords.Count);
            await Assert.That(root.GetProperty("calls")[0].GetProperty("tool").GetString())
                .IsEqualTo("list_levels");
            await Assert.That(root.GetProperty("transcriptPath").GetString()).IsEqualTo(markdownPath);

            // The tool call really went through the production loop.
            await Assert.That(toolRecords.Count).IsEqualTo(1);
            await Assert.That(toolRecords[0].ToolName).IsEqualTo("list_levels");
            await Assert.That(toolRecords[0].Status).IsEqualTo("ok");
            await Assert.That(result.ToolCallCount).IsEqualTo(1);
            await Assert.That(result.Rounds).IsEqualTo(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SMARTCON_BENCH_RESULTS_DIR", previousRoot);
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
}
