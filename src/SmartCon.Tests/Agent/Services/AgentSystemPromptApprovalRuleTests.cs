using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// C7-10: the dry-run rule must not contradict the AutoApproveSession mode
/// section. In that mode nobody answers, so "wait for confirmation" would stall
/// every write.
/// </summary>
public sealed class AgentSystemPromptApprovalRuleTests
{
    [Fact]
    public void StaticPrompt_DryRunRuleNamesTheAutoApproveException()
    {
        var prompt = AgentSystemPrompt.BuildStatic();

        Assert.Contains("wait for explicit user confirmation", prompt, StringComparison.Ordinal);
        Assert.Contains("AutoApproveSession", prompt, StringComparison.Ordinal);
        Assert.Contains("do not wait", prompt, StringComparison.Ordinal);
        Assert.Contains("The dry run itself is never skipped", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticPrompt_StillCarriesNoModeSection()
    {
        // The rule names the mode; the mode itself stays in the per-turn tail,
        // so the static prefix keeps its cache hash.
        var prompt = AgentSystemPrompt.BuildStatic();

        Assert.DoesNotContain("## Approval mode", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("mode: ", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ModeSections_AgreeWithTheDryRunRule()
    {
        var auto = AgentSystemPrompt.ModeSection(AgentApprovalMode.AutoApproveSession);
        var confirm = AgentSystemPrompt.ModeSection(AgentApprovalMode.ConfirmWrites);

        Assert.Contains("without a per-action confirmation dialog", auto, StringComparison.Ordinal);
        Assert.Contains("requires explicit user confirmation", confirm, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticPrompt_ExceptionIsStatedOnce()
    {
        var prompt = AgentSystemPrompt.BuildStatic();

        var first = prompt.IndexOf("AutoApproveSession", StringComparison.Ordinal);
        Assert.True(first > 0);
        Assert.Equal(first, prompt.LastIndexOf("AutoApproveSession", StringComparison.Ordinal));
    }
}
