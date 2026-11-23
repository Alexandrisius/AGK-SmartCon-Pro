using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The honesty rules and the soft failures of the per-type differentiation
/// stage (the S4-S8 and S15-S21 probes of the review): the status says what the
/// answer was worth, and no failure of the stage costs a family its code.
/// Partial of <see cref="AgentClassificationMatcherTypeStageTests"/>.
/// </summary>
public sealed partial class AgentClassificationMatcherTypeStageTests
{
    private static readonly ClassifyTypeSnapshot[] ExternalInternal =
        { Type(1, "External"), Type(2, "Internal") };

    // ── Status honesty ──────────────────────────────────────────────────

    [Theory]
    [InlineData(PerTypeMode.LowSibling)]
    [InlineData(PerTypeMode.BareSibling)]
    public async Task Honesty_TypeAlreadyCarriesTheFamilyCode_UntrustedSiblingIsAlreadySet(PerTypeMode mode)
    {
        // S4: a re-run over coded types (onlyEmpty=false). A sibling nobody
        // trusts changes nothing for a type that already has the family code.
        var client = new Script { Mode = mode };

        var result = await MatchAsync(client, new[] { Type(1, "External", FamilyCode), Type(2, "Internal", FamilyCode) });

        Assert.Equal(2, client.PerTypeQuestions);
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.AlreadySet, item.Status);
            Assert.Equal(FamilyCode, item.Code);
        });
        Assert.Equal(2, result.AlreadySetItems.Count);
        Assert.Empty(result.SetParameterBatches);
        Assert.Empty(result.Assignments);
    }

    [Fact]
    public async Task Honesty_HonestNone_LandsInUncertainWithTheFamilyCodeAsCandidate()
    {
        // S5: the prompt promises "an honest none is better than a wrong code"
        // - so a none is never dressed up as a confident family code.
        var client = new Script { Mode = PerTypeMode.None };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.Null(result.Error);
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.LowConfidence, item.Status);
            Assert.Equal(FamilyCode, item.Code);
            Assert.Equal(0, item.Score);
            Assert.Equal(
                "the model answered that neither the family code nor its siblings fit the type",
                item.Reason);
            Assert.Equal(FamilyCode, item.Candidates[0].Code);
        });
        Assert.Empty(result.SetParameterBatches);
        Assert.Empty(result.Assignments);
    }

    [Fact]
    public async Task Honesty_FamilyCodeBarelyConfirmedForTheType_LandsInUncertain()
    {
        // S16: the model confirmed the family code for the type with 0.2 - the
        // type does not inherit the family's 0.9 against the model's own doubt.
        var client = new Script { Mode = PerTypeMode.FamilyLow };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.Null(result.Error);
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.LowConfidence, item.Status);
            Assert.Equal(FamilyCode, item.Code);
            Assert.Equal(0.2, item.Score, 5);
        });
        Assert.Empty(result.SetParameterBatches);
        Assert.Empty(result.Assignments);
    }

    [Fact]
    public async Task Honesty_FamilyCodeConfirmedForTheType_KeepsTheFamilyScore()
    {
        // The keyword walk confirms the family code for both types with 0.9.
        var client = new Script();

        var result = await MatchAsync(client, ExternalInternal);

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(FamilyCode, item.Code);
            Assert.Equal(0.9, item.Score, 5);
        });
    }

    [Fact]
    public async Task Honesty_SiblingScore_IsTheWeakestLinkOfChainAndAnswer()
    {
        // S17: the family came down the table with 0.55 - a sibling answer of
        // 0.9 cannot lift the type above the branch it hangs on.
        var client = new Script { ChainScore = 0.55, Mode = PerTypeMode.Sibling };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(Sibling, item.Code);
            Assert.Equal(0.55, item.Score, 5);
        });
    }

    [Fact]
    public async Task Honesty_WeakSibling_KeepsTheFamilyCodeInUncertainWithTheSiblingToConfirm()
    {
        var client = new Script { Mode = PerTypeMode.LowSibling };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.LowConfidence, item.Status);
            Assert.Equal(FamilyCode, item.Code);
            Assert.Equal(0.9, item.Score, 5);
            Assert.Equal(Sibling, item.Candidates[0].Code);
            Assert.Equal(
                "the type text points to a sibling code below the confidence threshold",
                item.Reason);
        });
        Assert.Empty(result.SetParameterBatches);
    }

    [Fact]
    public async Task Honesty_LoweredMinScore_LetsTheWeakSiblingThrough()
    {
        // S21: the same 0.45 answer under minScore 0.3 is a proposal.
        var client = new Script { Mode = PerTypeMode.LowSibling };

        var result = await MatchAsync(client, ExternalInternal, minScore: 0.3);

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(Sibling, item.Code);
            Assert.Equal(0.45, item.Score, 5);
        });
    }

    [Fact]
    public async Task Honesty_TypeAlreadyCarriesTheSiblingCode_IsAlreadySet()
    {
        // S15: a confident sibling on a type that already stores it.
        var client = new Script { Mode = PerTypeMode.Sibling };

        var result = await MatchAsync(client, new[] { Type(1, "External", Sibling), Type(2, "Internal", Sibling) });

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.AlreadySet, item.Status);
            Assert.Equal(Sibling, item.Code);
        });
        Assert.Empty(result.SetParameterBatches);
    }

    [Fact]
    public async Task Honesty_BareSibling_IsAGuessAndStaysOutOfTheWrite()
    {
        // S19 (design decision): a sibling chosen without any confidence is a
        // guess - it re-codes nothing, and the type lands in uncertain.
        var client = new Script { Mode = PerTypeMode.BareSibling };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.NoConfidence, item.Status);
            Assert.Equal(FamilyCode, item.Code);
        });
        Assert.Empty(result.SetParameterBatches);
        Assert.Empty(result.Assignments);
    }

    // ── Soft failures: the family codes stand ───────────────────────────

    [Fact]
    public async Task SoftFailure_PerTypeCallThrows_KeepsTheFamilyCodesAndWarns()
    {
        // S8: three calls of four answered, the last one timed out.
        var client = new Script { Mode = PerTypeMode.Throw };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.Null(result.Error);
        Assert.False(result.Incomplete);
        Assert.All(result.Types, item => Assert.Equal(FamilyCode, item.Code));
        Assert.Equal(2, Assert.Single(result.SetParameterBatches).Count);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("2 type(s) of 1 family stay with the family code", warning, StringComparison.Ordinal);
        Assert.Contains(
            "decision layer failed (decision_call_failed: HttpClient timeout): 'Семейство Х' (typeIds 1, 2)",
            warning, StringComparison.Ordinal);
        Assert.Contains("[Action: accept the family codes", warning, StringComparison.Ordinal);
        // The failed call was made - and it counts.
        Assert.Equal(4, result.DecisionCalls);
    }

    [Fact]
    public async Task SoftFailure_PerTypeCallReturnsNoAnswers_KeepsTheFamilyCodesAndWarns()
    {
        // S7: an empty answer dictionary is a dead channel for the stage only.
        var client = new Script { Mode = PerTypeMode.Empty };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.Null(result.Error);
        Assert.False(result.Incomplete);
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(FamilyCode, item.Code);
        });
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(
            "decision layer failed (decision_unavailable: The decision client returned no answers)",
            warning, StringComparison.Ordinal);
        Assert.Equal(4, result.DecisionCalls);
    }

    [Fact]
    public async Task SoftFailure_RefusedQuestion_Warns_AndTheAnsweredTypesKeepTheirAnswers()
    {
        // S6: the second per-type question came back with a refusal.
        var client = new Script { Mode = PerTypeMode.Mixed, RefuseFromQuestion = 2 };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.Null(result.Error);
        Assert.False(result.Incomplete);
        Assert.Equal(FamilyCode, ResultOf(result, 1).Code);
        Assert.Equal(ClassifyTypeStatus.Proposed, ResultOf(result, 1).Status);
        // The refused type keeps the family code - and the run says so.
        Assert.Equal(FamilyCode, ResultOf(result, 2).Code);
        Assert.Equal(ClassifyTypeStatus.Proposed, ResultOf(result, 2).Status);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains(
            "1 type(s) of 1 family stay with the family code - the decision layer returned no " +
            "usable answer to their per-type question(s): 'Семейство Х' (typeIds 2)",
            warning, StringComparison.Ordinal);
        Assert.Contains("[Action:", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SoftFailure_RefusedSiblingAnswer_TheTypeKeepsTheFamilyCode()
    {
        // The refused question would have earned the sibling: a refusal is not
        // an answer, so the type stays with the family code, not with a guess.
        var client = new Script { Mode = PerTypeMode.Mixed, RefuseFromQuestion = 2 };

        var result = await MatchAsync(client, new[] { Type(1, "External", FamilyCode), Type(2, "Internal", FamilyCode) });

        Assert.Equal(ClassifyTypeStatus.AlreadySet, ResultOf(result, 2).Status);
        Assert.Equal(FamilyCode, ResultOf(result, 2).Code);
    }
}
