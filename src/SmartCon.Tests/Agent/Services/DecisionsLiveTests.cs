using SmartCon.Agent.Services.Decisions;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Opt-in live check of the System One decision layer (JEV analogue) against the
/// free Perplexity decider served by OpenRouter. Skipped silently unless
/// SMARTCON_DECIDER_LIVE=1; the API key comes from secrets.env, never from the
/// test body.
/// </summary>
public sealed class DecisionsLiveTests
{
    private static AgentDecisionsSettings? LiveSettings()
    {
        if (Environment.GetEnvironmentVariable("SMARTCON_DECIDER_LIVE") != "1")
            return null;

        var settings = AgentDecisionsSettings.Load();
        Assert.True(
            settings.IsAvailable,
            "Set SMARTCON_DECIDER_ENABLED=1 and an API key (secrets.env) before a live run.");
        return settings;
    }

    private static AgentToolDefinition Tool(string name, string description) =>
        new()
        {
            Name = name,
            Description = description,
            Risk = AgentToolRisk.Write,
            InputSchemaJson = @"{""type"":""object"",""properties"":{}}"
        };

    private static IReadOnlyList<AgentToolDefinition> Catalog() => new[]
    {
        Tool("rename_views", "Rename views by id"),
        Tool("list_views", "List project views"),
        Tool("set_view_crop", "Enable or edit the crop region of a view"),
        Tool("apply_view_template", "Apply a view template to views"),
        Tool("undo_last", "Undo the last SmartCon change")
    };

    [Fact]
    public async Task LiveClient_AnswersChoiceAndNoul()
    {
        var settings = LiveSettings();
        if (settings is null)
            return;

        using var client = new HttpDecisionClient(settings);
        var questions = new Dictionary<string, AgentDecisionQuestion>(StringComparer.Ordinal)
        {
            ["intent"] = AgentDecisionQuestion.Choice(
                "Which tool is the best match for the user's request?",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["rename_views"] = "Rename views",
                    ["list_views"] = "List views",
                    ["set_view_crop"] = "Edit the crop region"
                }),
            ["is_russian"] = AgentDecisionQuestion.Noul(
                "Is the user's request written in Russian?")
        };

        var answers = await client.AskAsync(
            "Переименуй вид SC_Eval_A в SC_Eval_B", questions, CancellationToken.None);

        Assert.True(answers.TryGetValue("intent", out var intent), "No intent answer.");
        Assert.True(intent.IsAnswered, "Intent was not answered: " + intent.Error);
        Assert.Equal("rename_views", intent.Choice);
        Assert.True(intent.Confidence is > 0 and <= 1);

        Assert.True(answers.TryGetValue("is_russian", out var language), "No language answer.");
        Assert.NotNull(language.Noul);
        Assert.True(language.Noul > 0.5, "Russian request was not detected.");
    }

    [Fact]
    public async Task LiveRouter_SelectsCropToolForRussianRequest()
    {
        var settings = LiveSettings();
        if (settings is null)
            return;

        using var client = new HttpDecisionClient(settings);
        var router = new DecisionsToolRouter(new RuleToolRouter(), client);

        var decision = await router.RouteAsync(
            "Включи область подрезки у вида SC_Eval_A", Catalog());

        Assert.Contains(decision.Tools, tool => tool.Name == "set_view_crop");
        Assert.True(decision.Tools.Count <= 14);
    }

}
