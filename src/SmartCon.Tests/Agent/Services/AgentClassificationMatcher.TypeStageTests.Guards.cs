using System.Globalization;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// The warning of the per-type stage must stay actionable (N1/N5 of the
/// review), a failing stage-2 must not feed the optional stage (N2), a digit
/// in a token is not always a size (N4), the wide-branch dilution of a
/// confirmed family code is pinned honestly (N8) and the two branches without
/// a unit test are covered (N6). Partial of
/// <see cref="AgentClassificationMatcherTypeStageTests"/>: shares the wall
/// table, the scripted client and the type helpers.
/// </summary>
public sealed partial class AgentClassificationMatcherTypeStageTests
{
    // ── The warning names what a re-run needs (N1/N5) ───────────────────

    [Fact]
    public async Task Leftover_InsideALoadedFamily_NamesTheFamilyIdAndAReRunOverAllItsTypes()
    {
        // N1: the budget ran out INSIDE a family - a re-run with exactly the
        // listed ids would ask nothing, so the warning hands over the family
        // handle instead of the ids alone.
        var client = new Script();
        var types = Enumerable.Range(1, 102)
            .Select(index => FamilyIdType(index, 1001, "Тип " + (char)('a' + index % 26) + (char)('a' + index / 26)))
            .Cast<ClassifyTypeSnapshot>()
            .ToList();

        var result = await MatchAsync(client, types);

        Assert.Equal(100, client.PerTypeQuestions);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("'Семейство Х' (familyId 1001; typeIds 101, 102)", warning, StringComparison.Ordinal);
        Assert.Contains(
            "[Action: accept the family codes, or to tell their types apart re-run classify_types " +
            "over ALL types of the named families - familyIds for a listed loadable family, its " +
            "name via nameContains for a system family - and pass onlyEmpty=false when their codes " +
            "are already written; a re-run with only the listed typeIds asks nothing]",
            warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leftover_BeyondTwentyFamilies_CountsTheUnnamedOnesAndNamesTheirCategories()
    {
        // N5: seventy-five families of two meanings spend the budget of 100 -
        // the leftovers of the last twenty-five must stay countable and
        // selectable, not dissolve into an unactionable tail.
        var client = new Script();
        var types = new List<ClassifyTypeSnapshot>();
        for (var family = 1; family <= 75; family++)
        {
            var name = "Семейство N" + family.ToString(CultureInfo.InvariantCulture);
            types.Add(new ClassifyTypeSnapshot(
                2 * family - 1, "Категория", name, family, "Внешний", null, Array.Empty<string>()));
            types.Add(new ClassifyTypeSnapshot(
                2 * family, "Категория", name, family, "Внутренний", null, Array.Empty<string>()));
        }

        var result = await MatchAsync(client, types);

        Assert.Equal(100, client.PerTypeQuestions);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("50 type(s) of 25 families stay with the family code", warning, StringComparison.Ordinal);
        Assert.Contains("'Семейство N51' (familyId 51; typeIds 101, 102)", warning, StringComparison.Ordinal);
        Assert.Contains(
            "and 10 more type(s) not listed - select the 5 unnamed families by their categories " +
            "(Категория) and the named ones by their familyIds",
            warning, StringComparison.Ordinal);
    }

    // ── A failing run does not feed the optional stage (N2) ─────────────

    [Fact]
    public async Task Stage2PartialError_SkipsThePerTypeCallInsteadOfSpendingItOnAFailingRun()
    {
        // A partial error of stage 2 keeps its typed error and fails the run -
        // the optional per-type stage must not spend decision calls on it.
        var client = new Script { BothLeafCodes = true, FailCall = 6 };
        var types = Enumerable.Range(1, 21)
            .Select(index => WallType(index, "Walls", "Alpha " + (char)('a' + index)))
            .Cast<ClassifyTypeSnapshot>()
            .Concat(new[] { OtherFamilyType(101, "External"), OtherFamilyType(102, "Internal") })
            .ToList();

        var result = await MatchAsync(client, types);

        Assert.Equal("decision_call_failed", result.Error);
        Assert.Equal(0, client.PerTypeQuestions);
    }

    // ── What a digit means (N4) ─────────────────────────────────────────

    [Theory]
    [InlineData("Марка EI30", "Марка EI60")]
    [InlineData("Бетон В20", "Бетон В25")]
    [InlineData("Клапан 15кч18п", "Клапан 15б1б")]
    public async Task NumberedMark_ThatNamesTheEssence_IsADifference(string first, string second)
    {
        // A digit does not make a token a size: a fire rating, a concrete grade
        // and a valve mark say WHAT the type is, not which size it is.
        var client = new Script();

        var result = await MatchAsync(client, new[] { Type(1, first), Type(2, second) });

        Assert.Equal(2, client.PerTypeQuestions);
    }

    [Fact]
    public async Task MixedDimensionNotation_IsNotADifference()
    {
        // «DN 50» and «Ду 80» say the same thing in two alphabets: the bare
        // dimension prefix is a size token like its number.
        var client = new Script();

        var result = await MatchAsync(client, new[] { Type(1, "Труба DN 50"), Type(2, "Труба Ду 80") });

        Assert.Equal(new[] { "branch", "descend", "descend" }, client.States.Select(Kind));
        Assert.Equal(0, client.PerTypeQuestions);
    }

    // ── The two branches without a unit test (N6) ───────────────────────

    [Fact]
    public async Task EmptyProbabilitiesAnswer_WarnsAndKeepsTheFamilyCodes()
    {
        // An answer that carries an empty probability set is usable for the
        // ranker (it raises no error) but ranks nothing - the stage says so
        // instead of passing the types silently.
        var client = new Script { Mode = PerTypeMode.EmptyProbabilities };

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
            "decision layer returned no usable answer to their per-type question(s)",
            warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BareFamilyCodeForTheType_TheFamilyScoreStands()
    {
        // The per-type answer confirmed the family code as a bare choice: the
        // bare answer adds nothing - the family chain score decides.
        var client = new Script { Mode = PerTypeMode.BareFamily };

        var result = await MatchAsync(client, ExternalInternal);

        Assert.Null(result.Error);
        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(FamilyCode, item.Code);
            Assert.Equal(0.9, item.Score, 5);
        });
        Assert.Equal(2, Assert.Single(result.SetParameterBatches).Count);
    }

    // ── The wide-branch dilution, measured (N8) ─────────────────────────

    [Fact]
    public async Task WideBranch_ConfidentFamilyCode_StaysProposed()
    {
        // Forty sibling leaves do not dilute a confident confirmation: the
        // family code survives as a proposal.
        var client = new Script { Mode = PerTypeMode.FamilyDiluted, DilutedScore = 0.9 };

        var result = await MatchAsync(client, ExternalInternal, WideWallTable());

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.Proposed, item.Status);
            Assert.Equal(FamilyCode, item.Code);
        });
        Assert.Equal(2, Assert.Single(result.Assignments).TypeIdCount);
    }

    [Fact]
    public async Task WideBranch_DilutedFamilyCode_LandsInUncertainWithTheFamilyCodeToConfirm()
    {
        // The model picked the family code as its top option but the wide branch
        // diluted the probability below the threshold: the type lands in
        // uncertain WITH the family code as the first candidate to confirm.
        // Pinned deliberately (N8): lifting the cut would re-open S16 - the
        // model's own doubt must count against the family chain.
        var client = new Script { Mode = PerTypeMode.FamilyDiluted, DilutedScore = 0.4 };

        var result = await MatchAsync(client, ExternalInternal, WideWallTable());

        Assert.All(result.Types, item =>
        {
            Assert.Equal(ClassifyTypeStatus.LowConfidence, item.Status);
            Assert.Equal(FamilyCode, item.Code);
            Assert.Equal(0.4, item.Score, 5);
            Assert.Equal(FamilyCode, item.Candidates[0].Code);
        });
        Assert.Empty(result.SetParameterBatches);
        Assert.Empty(result.Assignments);
    }

    // ── Support ─────────────────────────────────────────────────────────

    private static ClassifyTypeSnapshot FamilyIdType(long id, long familyId, string typeName) =>
        new(id, "Категория", "Семейство Х", familyId, typeName, null, Array.Empty<string>());

    private static ClassifyTypeSnapshot OtherFamilyType(long id, string typeName) =>
        new(id, "Категория", "Семейство У", null, typeName, null, Array.Empty<string>());

    /// <summary>Forty sibling leaves under one parent: a wide branch.</summary>
    private static AgentClassificationTable WideWallTable()
    {
        var rows = new List<AgentClassificationEntry>
        {
            Row("10", "Shell", 1, null),
            Row("10.10", "Walls", 2, "10")
        };
        for (var index = 1; index <= 40; index++)
        {
            rows.Add(Row(
                "10.10." + index.ToString(CultureInfo.InvariantCulture),
                "Wall kind " + (char)('a' + index % 26),
                3,
                "10.10"));
        }

        rows.Add(Row("20", "Services", 1, null));
        return AgentClassificationTable.Build(rows, null);
    }

    /// <summary>The scripted client: the modes and the call failure of this partial.</summary>
    private sealed partial class Script
    {
        /// <summary>The 1-based AskAsync call to fail with a transport error (0: none).</summary>
        public int FailCall { get; set; }

        /// <summary>The probability a diluted family-code confirmation carries.</summary>
        public double DilutedScore { get; set; } = 0.9;

        /// <summary>
        /// The answers of the modes this partial adds; null leaves the question
        /// to the modes of the main partial.
        /// </summary>
        public AgentDecisionAnswer? GuardAnswer(AgentDecisionQuestion question)
        {
            switch (Mode)
            {
                case PerTypeMode.EmptyProbabilities:
                    return new AgentDecisionAnswer
                    {
                        Type = "choice",
                        Probabilities = new Dictionary<string, double>()
                    };
                case PerTypeMode.BareFamily:
                    return new AgentDecisionAnswer { Type = "choice", Choice = Code };
                case PerTypeMode.FamilyDiluted:
                    return Prob(Code, DilutedScore);
                default:
                    return null;
            }
        }
    }
}
