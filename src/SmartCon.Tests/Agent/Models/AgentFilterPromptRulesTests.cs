using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

/// <summary>
/// #400 prompt rules: the static prefix must teach the model the two filter
/// checkboxes, the read-back after a failure and the pre-flight. The prefix is
/// a constant, so these assertions also protect the prompt cache (P.3): the
/// section may only change together with the hash test.
/// </summary>
public sealed class AgentFilterPromptRulesTests
{
    [Fact]
    public void StaticPrompt_ExplainsBothFilterCheckboxes()
    {
        var prompt = AgentSystemPrompt.BuildStatic();

        Assert.Contains("hide = filter enabled + visibility off", prompt);
        Assert.Contains("none = filter disabled", prompt);
        Assert.Contains("«Видимость»", prompt);
    }

    [Fact]
    public void StaticPrompt_RequiresReadBackAfterFailureAndPreflight()
    {
        var prompt = AgentSystemPrompt.BuildStatic();

        Assert.Contains("never claim \"nothing changed\"", prompt);
        Assert.Contains("undoSupported=false", prompt);
        Assert.Contains("list_filterable_parameters", prompt);
        Assert.Contains("supportedByAllCategories=true", prompt);
        Assert.Contains("errorCode=no_changes", prompt);
    }

    [Fact]
    public void StaticPrompt_DoesNotContainPerTurnData()
    {
        // Guard for the prompt cache (P.3/P.5): the cacheable prefix must not
        // carry a date, a counter, a document id or a selection.
        var prompt = AgentSystemPrompt.BuildStatic();

        Assert.DoesNotContain("<turn_context>", prompt);
        Assert.DoesNotContain("<session_context>", prompt);
        Assert.DoesNotContain("date: ", prompt);
        Assert.DoesNotContain("document_id:", prompt);
        Assert.DoesNotContain("selection:", prompt);
    }
}