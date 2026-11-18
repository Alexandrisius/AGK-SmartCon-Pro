using Xunit;
using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentTokenEstimatorTests
{
    [Fact]
    public void Estimate_EmptyMessage_ReturnsOverhead()
    {
        var estimate = AgentTokenEstimator.Estimate(AgentMessage.User(string.Empty));

        Assert.Equal(4, estimate);
    }

    [Fact]
    public void Estimate_ImageAttachment_AddsImageBudget()
    {
        var message = AgentMessage.User("screen", new[]
        {
            new AgentAttachment
            {
                FileName = "a.png",
                MimeType = "image/png",
                Base64Data = "AAAA"
            }
        });

        var estimate = AgentTokenEstimator.Estimate(message);

        Assert.True(estimate >= AgentTokenEstimator.ImageTokenEstimate);
    }

    [Fact]
    public void Estimate_LongRussianText_IsHigherThanEnglishOfSameLength()
    {
        var english = AgentMessage.User(new string('a', 500));
        var russian = AgentMessage.User(new string('а', 500));

        var englishEstimate = AgentTokenEstimator.Estimate(english);
        var russianEstimate = AgentTokenEstimator.Estimate(russian);

        Assert.True(russianEstimate > englishEstimate);
    }

    [Fact]
    public void Estimate_MultipleMessages_SumsIndividualEstimates()
    {
        var messages = new[] { AgentMessage.User("hello"), AgentMessage.Assistant("world") };

        Assert.Equal(
            AgentTokenEstimator.Estimate(messages),
            AgentTokenEstimator.Estimate(messages[0]) + AgentTokenEstimator.Estimate(messages[1]));
    }

    [Fact]
    public void EstimateText_CyrillicTextIsMoreExpensiveThanEnglish()
    {
        var english = AgentTokenEstimator.EstimateText(new string('a', 500));
        var russian = AgentTokenEstimator.EstimateText(new string('а', 500));

        Assert.True(english > 0);
        Assert.True(russian > english);
    }

    [Fact]
    public void EstimateTools_IncludesNameDescriptionAndSchema()
    {
        var tools = new[]
        {
            new AgentToolDefinition
            {
                Name = "get_views",
                Description = "Lists project views with optional filters.",
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}"
            }
        };

        Assert.True(AgentTokenEstimator.EstimateTools(tools) > 0);
    }

    [Fact]
    public void EstimateRequest_AddsSystemAndToolsOnTopOfMessages()
    {
        var messages = new[] { AgentMessage.User("hello") };
        var tools = new[]
        {
            new AgentToolDefinition
            {
                Name = "get_views",
                Description = "Lists project views with optional filters.",
                InputSchemaJson = "{\"type\":\"object\"}"
            }
        };

        var requestEstimate = AgentTokenEstimator.EstimateRequest(
            new string('s', 1000), messages, tools);

        Assert.True(requestEstimate > AgentTokenEstimator.Estimate(messages));
    }
}