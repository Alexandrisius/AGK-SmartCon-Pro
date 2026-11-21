using System.Text.Json;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentDeleteConfirmTests
{
    private static JsonElement? Arg(string json) =>
        json is null
            ? null
            : JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Verify_MatchingSets_Ok()
    {
        var result = AgentDeleteConfirm.Verify(
            new[] { "Вид Б", "Вид А", "Вид В" },
            Arg("""["Вид В","Вид А","Вид Б"]"""));

        Assert.True(result.Ok);
        Assert.Null(result.ErrorCode);
        Assert.Equal(new[] { "Вид А", "Вид Б", "Вид В" }, result.Expected);
    }

    [Fact]
    public void Verify_MissingArgument_Required()
    {
        var result = AgentDeleteConfirm.Verify(new[] { "A" }, null);

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.RequiredCode, result.ErrorCode);
        Assert.Equal(new[] { "A" }, result.Expected);
    }

    [Fact]
    public void Verify_EmptyArray_Required()
    {
        var result = AgentDeleteConfirm.Verify(new[] { "A", "B" }, Arg("[]"));

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.RequiredCode, result.ErrorCode);
    }

    [Fact]
    public void Verify_NonArrayArgument_Required()
    {
        var result = AgentDeleteConfirm.Verify(new[] { "A" }, Arg("\"A\""));

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.RequiredCode, result.ErrorCode);
    }

    [Fact]
    public void Verify_Mismatch_ReportsMissingAndUnexpected()
    {
        var result = AgentDeleteConfirm.Verify(
            new[] { "A", "B", "C" },
            Arg("""["A","C","X"]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.MismatchCode, result.ErrorCode);
        Assert.Equal(new[] { "B" }, result.Missing);
        Assert.Equal(new[] { "X" }, result.Unexpected);
        Assert.Equal(new[] { "A", "B", "C" }, result.Expected);
        Assert.Equal(new[] { "A", "C", "X" }, result.Got);
    }

    [Fact]
    public void Verify_StaleListAfterModelChange_Mismatch()
    {
        var dryRunNames = new[] { "A", "B" };
        var result = AgentDeleteConfirm.Verify(
            new[] { "A", "B", "NEW" },
            Arg("""["A","B"]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.MismatchCode, result.ErrorCode);
        Assert.Equal(new[] { "NEW" }, result.Missing);
        Assert.Empty(result.Unexpected);
    }

    [Fact]
    public void Verify_DuplicatesIgnored_Ok()
    {
        var result = AgentDeleteConfirm.Verify(
            new[] { "A", "A", "B" },
            Arg("""["B","B","A","A"]"""));

        Assert.True(result.Ok);
        Assert.Equal(new[] { "A", "B" }, result.Expected);
    }

    [Fact]
    public void Verify_OrdinalComparison_CaseDiffersIsMismatch()
    {
        var result = AgentDeleteConfirm.Verify(
            new[] { "Wall" },
            Arg("""["wall"]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.MismatchCode, result.ErrorCode);
        Assert.Equal(new[] { "Wall" }, result.Missing);
        Assert.Equal(new[] { "wall" }, result.Unexpected);
    }

    [Fact]
    public void Verify_EmptyFreshNames_AnyConfirmationIsMismatch()
    {
        var result = AgentDeleteConfirm.Verify(
            Array.Empty<string>(),
            Arg("""["A"]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.MismatchCode, result.ErrorCode);
        Assert.Equal(new[] { "A" }, result.Unexpected);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void Verify_WhitespaceEntriesInConfirmation_IgnoredThenRequired()
    {
        var result = AgentDeleteConfirm.Verify(
            new[] { "A" },
            Arg("""["  ",""]"""));

        Assert.False(result.Ok);
        Assert.Equal(AgentDeleteConfirm.RequiredCode, result.ErrorCode);
    }

    [Fact]
    public void Verify_NonStringEntries_Ignored()
    {
        var result = AgentDeleteConfirm.Verify(
            new[] { "A" },
            Arg("""["A",42,null]"""));

        Assert.True(result.Ok);
    }
}
