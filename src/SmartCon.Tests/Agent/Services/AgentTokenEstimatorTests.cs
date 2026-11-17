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
}