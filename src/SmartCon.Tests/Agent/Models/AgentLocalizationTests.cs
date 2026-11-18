using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

public sealed class AgentLocalizationTests
{
    [Theory]
    [InlineData("Agent_Approval_Confirm")]
    [InlineData("Agent_Approval_Auto")]
    [InlineData("Agent_Approval_ReadOnly")]
    [InlineData("Agent_Approval_ReadOnlyBanner")]
    [InlineData("Agent_Approval_AutoBanner")]
    [InlineData("Agent_Approval_Tooltip")]
    [InlineData("Agent_Approval_Disable")]
    [InlineData("Agent_Confirm_Risks")]
    [InlineData("Agent_Risk_Destructive")]
    [InlineData("Agent_Risk_Plan")]
    [InlineData("Agent_Risk_Discard")]
    [InlineData("Agent_Settings_DecisionModel")]
    [InlineData("Agent_Settings_DecisionHint")]
    [InlineData("Agent_Decision_Off")]
    public void ApprovalAndRiskKeys_HaveRuAndEnText(string key)
    {
        Assert.True(LocalizationService.ContainsKey(key));
        Assert.NotEqual(key, LocalizationService.GetString(key, Language.RU));
        Assert.NotEqual(key, LocalizationService.GetString(key, Language.EN));
    }
}
