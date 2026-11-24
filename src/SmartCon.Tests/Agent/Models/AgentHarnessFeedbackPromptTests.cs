using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

/// <summary>
/// #523: the harness feedback block is the owner's only channel for growing the
/// harness. It must classify every point (defect / convenience / critical) and
/// keep the ~150-tool catalog from growing for one-session comfort.
/// </summary>
public sealed class AgentHarnessFeedbackPromptTests
{
    private static string Prompt => AgentSystemPrompt.BuildStatic();

    [Fact]
    public void StaticPrompt_ClassifiesHarnessFeedback()
    {
        var prompt = Prompt;

        Assert.Contains("Classify every point", prompt, StringComparison.Ordinal);
        Assert.Contains("defect —", prompt, StringComparison.Ordinal);
        Assert.Contains("convenience —", prompt, StringComparison.Ordinal);
        Assert.Contains("critical —", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticPrompt_AllowsNewToolsOnlyForCriticalPoints()
    {
        var prompt = Prompt;

        Assert.Contains(
            "Request a new tool or argument ONLY for a critical point",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "not a reason to grow a ~150-tool catalog",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("no new friction", prompt, StringComparison.Ordinal);
    }
}