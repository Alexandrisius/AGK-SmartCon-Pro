using System.Text.Json;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// A pack skill is an operating manual for risky work: load_tool_pack delivers it
/// for an explicitly requested pack and for the pack of a write tool. A read-only
/// tool only gets its pack announced, so one lookup no longer inlines the whole
/// manual of its pack.
/// </summary>
public sealed class LoadToolPackSkillPolicyTests
{
    private const string FamiliesHint =
        """Operating manual(s) available for pack(s) families; call load_tool_pack(pack="families") before you modify anything in that area — read-only tools need no manual.""";

    private const string MixedHint =
        """Operating manual(s) available for pack(s) model, views; call load_tool_pack(pack="model"), load_tool_pack(pack="views") before you modify anything in that area — read-only tools need no manual.""";

    private sealed class Harness
    {
        public AgentSessionContext Session { get; } = new();

        public FakeAgentLlmClient Llm { get; } = new();

        public AgentOrchestrator Orchestrator { get; }

        public Harness(params IAgentTool[] tools)
        {
            var loadTool = new LoadToolPackTool(Session);
            var registry = new AgentToolRegistry(
                new IAgentTool[] { loadTool }.Concat(tools), null, loadTool);
            Orchestrator = AgentHarness.Create(
                Llm, registry, new InMemoryConversationStore(), session: Session);
        }

        /// <summary>Root JSON of every tool result of the turn, in call order.</summary>
        public async Task<List<JsonElement>> RunAsync(params AgentToolCall[] calls)
        {
            foreach (var call in calls)
                Llm.EnqueueAssistant(null, new[] { call });
            Llm.EnqueueAssistant("готово");

            await Orchestrator.RunTurnAsync(
                Guid.NewGuid(), "задача", Array.Empty<AgentAttachment>(), null,
                CancellationToken.None);

            return Llm.Requests.Last()
                .Messages
                .Where(message => message.Role == AgentRole.Tool)
                .Select(message => Parse(message.Content))
                .ToList();
        }
    }

    private static AgentToolCall Load(string id, string argumentsJson) => new()
    {
        Id = id,
        Name = LoadToolPackTool.ToolName,
        ArgumentsJson = argumentsJson
    };

    private static AgentToolCall Call(string id, string name) => new()
    {
        Id = id,
        Name = name,
        ArgumentsJson = "{}"
    };

    private static JsonElement Parse(string? json)
    {
        using var document = JsonDocument.Parse(json ?? string.Empty);
        return document.RootElement.Clone();
    }

    private static JsonElement Data(JsonElement loadResult) => loadResult.GetProperty("data");

    private static string[] SkillPacks(JsonElement container) =>
        container.GetProperty("skills").EnumerateArray()
            .Select(skill => skill.GetProperty("pack").GetString()!)
            .ToArray();

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString()!).ToArray();

    [Fact]
    public async Task ReadOnlyToolByName_AnnouncesThePackInsteadOfInliningItsSkill()
    {
        var harness = new Harness(new ScriptedTool("list_shared_parameters", AgentToolRisk.Read));

        var data = Data((await harness.RunAsync(
            Load("call_1", """{"tools":["list_shared_parameters"]}""")))[0]);

        Assert.Equal(JsonValueKind.Null, data.GetProperty("skills").ValueKind);
        Assert.Equal(new[] { "families" }, Strings(data.GetProperty("skillsAvailable")));
        Assert.Equal(FamiliesHint, data.GetProperty("skillHint").GetString());
        Assert.Contains("list_shared_parameters", Strings(data.GetProperty("loaded")));
        Assert.Empty(harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task ReadOnlyToolThenPackRequest_DeliversTheSkillExactlyOnce()
    {
        var harness = new Harness(new ScriptedTool("list_shared_parameters", AgentToolRisk.Read));

        var results = await harness.RunAsync(
            Load("call_1", """{"tools":["list_shared_parameters"]}"""),
            Load("call_2", """{"pack":"families"}"""),
            Load("call_3", """{"pack":"families"}"""));

        Assert.Equal(JsonValueKind.Null, Data(results[0]).GetProperty("skills").ValueKind);
        Assert.Equal(new[] { "families" }, SkillPacks(Data(results[1])));
        Assert.Equal(JsonValueKind.Null, Data(results[2]).GetProperty("skills").ValueKind);
        Assert.Contains("families", Strings(Data(results[2]).GetProperty("skillAlreadySent")));
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task WriteToolByName_InlinesThePackSkillOnce()
    {
        var harness = new Harness(
            new ScriptedTool("set_family_parameter_values", AgentToolRisk.Write));

        var results = await harness.RunAsync(
            Load("call_1", """{"tools":["set_family_parameter_values"]}"""),
            Call("call_2", "set_family_parameter_values"));

        var data = Data(results[0]);
        Assert.Equal(new[] { "families" }, SkillPacks(data));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillsAvailable").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillHint").ValueKind);
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);

        // The first write of the pack tool must not attach the manual a second time.
        Assert.False(results[1].TryGetProperty("skills", out _));
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task ExplicitPack_InlinesTheSkillAsBefore()
    {
        var harness = new Harness(new ScriptedTool("semantic_match", AgentToolRisk.Read));

        var data = Data((await harness.RunAsync(Load("call_1", """{"pack":"semantic"}""")))[0]);

        Assert.Equal(new[] { "semantic" }, SkillPacks(data));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillsAvailable").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillHint").ValueKind);
        Assert.Equal(new[] { "semantic" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task MixedRequest_InlinesTheWriteSideAndAnnouncesTheReadOnlySide()
    {
        var harness = new Harness(
            new ScriptedTool("set_family_parameter_values", AgentToolRisk.Write),
            new ScriptedTool("list_views", AgentToolRisk.Read),
            new ScriptedTool("get_project_overview", AgentToolRisk.Read));

        var data = Data((await harness.RunAsync(Load(
            "call_1",
            """{"tools":["set_family_parameter_values","list_views","get_project_overview"]}""")))[0]);

        Assert.Equal(new[] { "families" }, SkillPacks(data));
        Assert.Equal(new[] { "model", "views" }, Strings(data.GetProperty("skillsAvailable")));
        Assert.Equal(MixedHint, data.GetProperty("skillHint").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillPackNote").ValueKind);
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task SecondIdenticalReadOnlyCall_InlinesNothing()
    {
        var harness = new Harness(new ScriptedTool("list_shared_parameters", AgentToolRisk.Read));

        var results = await harness.RunAsync(
            Load("call_1", """{"tools":["list_shared_parameters"]}"""),
            Load("call_2", """{"tools":["list_shared_parameters"]}"""));

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            var data = Data(result);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("skills").ValueKind);
            Assert.Equal(new[] { "families" }, Strings(data.GetProperty("skillsAvailable")));
        });
        Assert.Empty(harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task WriteAndReadOnlyToolOfOnePack_InlinesTheSkillWithoutAnnouncement()
    {
        var harness = new Harness(
            new ScriptedTool("set_family_parameter_values", AgentToolRisk.Write),
            new ScriptedTool("list_shared_parameters", AgentToolRisk.Read));

        var data = Data((await harness.RunAsync(Load(
            "call_1",
            """{"tools":["set_family_parameter_values","list_shared_parameters"]}""")))[0]);

        Assert.Equal(new[] { "families" }, SkillPacks(data));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillsAvailable").ValueKind);
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task ExplicitPackWithAReadOnlyToolOfTheSamePack_DeliversTheSkillOnce()
    {
        var harness = new Harness(new ScriptedTool("list_shared_parameters", AgentToolRisk.Read));

        var data = Data((await harness.RunAsync(Load(
            "call_1",
            """{"pack":"families","tools":["list_shared_parameters"]}""")))[0]);

        Assert.Equal(new[] { "families" }, SkillPacks(data));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillsAvailable").ValueKind);
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task ReadOnlyToolOfAnAlreadySentPack_ReportsItAsSentWithoutAnnouncement()
    {
        var harness = new Harness(new ScriptedTool("list_shared_parameters", AgentToolRisk.Read));

        var results = await harness.RunAsync(
            Load("call_1", """{"pack":"families"}"""),
            Load("call_2", """{"tools":["list_shared_parameters"]}"""));

        var data = Data(results[1]);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skills").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillsAvailable").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillHint").ValueKind);
        Assert.Contains("families", Strings(data.GetProperty("skillAlreadySent")));
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task ReadOnlyToolByName_LeavesTheManualForTheFirstWrite()
    {
        // The announcement is not a waiver: a write tool of the announced pack that
        // runs without a pack request still gets the manual with its first result.
        var harness = new Harness(
            new ScriptedTool("list_shared_parameters", AgentToolRisk.Read),
            new ScriptedTool("set_family_parameter_values", AgentToolRisk.Write));

        var results = await harness.RunAsync(
            Load("call_1", """{"tools":["list_shared_parameters"]}"""),
            Call("call_2", "set_family_parameter_values"));

        Assert.Equal(JsonValueKind.Null, Data(results[0]).GetProperty("skills").ValueKind);
        Assert.Equal(new[] { "families" }, SkillPacks(results[1]));
        Assert.Equal(new[] { "families" }, harness.Session.SentPackSkills);
    }

    [Fact]
    public async Task CapReachedByExplicitPacks_ReportsTheWithheldWriteSideManual()
    {
        var harness = new Harness(new ScriptedTool("rename_schedule", AgentToolRisk.Write));

        var data = Data((await harness.RunAsync(Load("call_1",
            """{"packs":["views","levels","filters","families","templates"],"tools":["rename_schedule"]}""")))[0]);

        Assert.Equal(5, SkillPacks(data).Length);
        Assert.DoesNotContain("schedules", SkillPacks(data));
        Assert.Equal(JsonValueKind.Null, data.GetProperty("skillsAvailable").ValueKind);
        Assert.Contains(
            "5 of 6",
            data.GetProperty("skillPackNote").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(5, harness.Session.SentPackSkills.Count);
    }
}
