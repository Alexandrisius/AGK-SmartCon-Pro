using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class CtcGuesserTests
{
    private static ConnectionTypeCode Ctc(int v) => new(v);

    private static FittingMappingRule DirectRule(int from, int to) => new()
    {
        FromType = Ctc(from),
        ToType = Ctc(to),
        IsDirectConnect = true,
    };

    private static FittingMappingRule AdapterRule(int from, int to) => new()
    {
        FromType = Ctc(from),
        ToType = Ctc(to),
        IsDirectConnect = false,
        FittingFamilies = [new FittingMapping { FamilyName = "Adapter", SymbolName = "*", Priority = 1 }],
    };

    // ── CanDirectConnect ───────────────────────────────────────────────────

    [Fact]
    public void CanDirectConnect_SymmetricRule_ReturnsTrue()
    {
        var rules = new[] { DirectRule(2, 3) };
        Assert.True(CtcGuesser.CanDirectConnect(Ctc(2), Ctc(3), rules));
        Assert.True(CtcGuesser.CanDirectConnect(Ctc(3), Ctc(2), rules));
    }

    [Fact]
    public void CanDirectConnect_NonDirectRule_ReturnsFalse()
    {
        var rules = new[] { AdapterRule(2, 3) };
        Assert.False(CtcGuesser.CanDirectConnect(Ctc(2), Ctc(3), rules));
    }

    [Fact]
    public void CanDirectConnect_SelfLoop_ReturnsTrue()
    {
        var rules = new[] { DirectRule(1, 1) };
        Assert.True(CtcGuesser.CanDirectConnect(Ctc(1), Ctc(1), rules));
    }

    // ── FindDirectConnectCounterpart ──────────────────────────────────────

    [Fact]
    public void FindCounterpart_FromSide_ReturnsTo()
    {
        var rules = new[] { DirectRule(2, 3) };
        var result = CtcGuesser.FindDirectConnectCounterpart(Ctc(2), rules);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public void FindCounterpart_ToSide_ReturnsFrom()
    {
        var rules = new[] { DirectRule(2, 3) };
        var result = CtcGuesser.FindDirectConnectCounterpart(Ctc(3), rules);
        Assert.Equal(2, result.Value);
    }

    [Fact]
    public void FindCounterpart_NotFound_ReturnsUndefined()
    {
        var rules = new[] { DirectRule(2, 3) };
        var result = CtcGuesser.FindDirectConnectCounterpart(Ctc(99), rules);
        Assert.False(result.IsDefined);
    }

    [Fact]
    public void FindCounterpart_UndefinedInput_ReturnsUndefined()
    {
        var rules = new[] { DirectRule(2, 3) };
        var result = CtcGuesser.FindDirectConnectCounterpart(ConnectionTypeCode.Undefined, rules);
        Assert.False(result.IsDefined);
    }

    [Fact]
    public void FindCounterpart_SkipsNonDirect()
    {
        var rules = new FittingMappingRule[] { AdapterRule(2, 5), DirectRule(2, 3) };
        var result = CtcGuesser.FindDirectConnectCounterpart(Ctc(2), rules);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public void FindCounterpart_MultipleDirectRules_ReturnsFirst()
    {
        var rules = new[] { DirectRule(2, 3), DirectRule(2, 7) };
        var result = CtcGuesser.FindDirectConnectCounterpart(Ctc(2), rules);
        Assert.Equal(3, result.Value);
    }

    [Fact]
    public void FindCounterpart_EmptyRules_ReturnsUndefined()
    {
        var result = CtcGuesser.FindDirectConnectCounterpart(Ctc(1), Array.Empty<FittingMappingRule>());
        Assert.False(result.IsDefined);
    }

    // ── GuessAdapterCtc ──────────────────────────────────────────────────

    [Fact]
    public void GuessAdapter_CrossConnect_ReturnsCounterparts()
    {
        // static=ВР(2), dynamic=муфтаПП(5)
        // direct: ВР(2)↔НР(3), муфтаПП(5)↔гладкийПП(6)
        var rules = new[] { DirectRule(2, 3), DirectRule(5, 6) };
        var (forStatic, forDynamic) = CtcGuesser.GuessAdapterCtc(Ctc(2), Ctc(5), rules);

        Assert.Equal(3, forStatic.Value);  // НР — counterpart для ВР
        Assert.Equal(6, forDynamic.Value); // гладкий конец — counterpart для муфты ПП
    }

    [Fact]
    public void GuessAdapter_SameType_ReturnsCounterpart()
    {
        // static=ВР(2), dynamic=ВР(2) → counterpart(ВР)=НР(3) → фитинг: НР-НР
        var rules = new[] { DirectRule(2, 3) };
        var (forStatic, forDynamic) = CtcGuesser.GuessAdapterCtc(Ctc(2), Ctc(2), rules);

        Assert.Equal(3, forStatic.Value);  // НР — counterpart для ВР
        Assert.Equal(3, forDynamic.Value); // НР — counterpart для ВР
    }

    [Fact]
    public void GuessAdapter_SameType_SelfLoop_ReturnsSame()
    {
        // static=Сварка(1), dynamic=Сварка(1) → counterpart(Сварка)=Сварка → фитинг: Сварка-Сварка
        var rules = new[] { DirectRule(1, 1) };
        var (forStatic, forDynamic) = CtcGuesser.GuessAdapterCtc(Ctc(1), Ctc(1), rules);

        Assert.Equal(1, forStatic.Value);
        Assert.Equal(1, forDynamic.Value);
    }

    [Fact]
    public void GuessAdapter_NoDirectRule_FallsBackToOriginal()
    {
        var rules = Array.Empty<FittingMappingRule>();
        var (forStatic, forDynamic) = CtcGuesser.GuessAdapterCtc(Ctc(2), Ctc(5), rules);

        Assert.Equal(2, forStatic.Value);  // fallback to staticCTC
        Assert.Equal(5, forDynamic.Value); // fallback to dynamicCTC
    }

    [Fact]
    public void GuessAdapter_PartialMatch_FallsBackPartially()
    {
        // direct exists only for static side
        var rules = new[] { DirectRule(2, 3) };
        var (forStatic, forDynamic) = CtcGuesser.GuessAdapterCtc(Ctc(2), Ctc(99), rules);

        Assert.Equal(3, forStatic.Value);   // counterpart found
        Assert.Equal(99, forDynamic.Value); // fallback — no direct rule for 99
    }

    // ── GuessReducerCtc ──────────────────────────────────────────────────

    [Fact]
    public void GuessReducer_SameType_BothSame()
    {
        var rules = Array.Empty<FittingMappingRule>();
        var (forStatic, forDynamic) = CtcGuesser.GuessReducerCtc(Ctc(1), Ctc(1), rules);
        Assert.Equal(1, forStatic.Value);
        Assert.Equal(1, forDynamic.Value);
    }

    [Fact]
    public void GuessReducer_CrossType_UsesDirectConnect()
    {
        // static=НР(2), dynamic=Сварка(1)
        // direct: НР(2)↔ВР(3), Сварка(1)↔Сварка(1)
        var rules = new[] { DirectRule(2, 3), DirectRule(1, 1) };
        var (forStatic, forDynamic) = CtcGuesser.GuessReducerCtc(Ctc(2), Ctc(1), rules);

        Assert.Equal(3, forStatic.Value);  // ВР — counterpart для НР
        Assert.Equal(1, forDynamic.Value); // Сварка — counterpart для Сварки
    }

    [Fact]
    public void GuessReducer_CrossType_NoDirectRules_FallbackToReverse()
    {
        var rules = Array.Empty<FittingMappingRule>();
        var (forStatic, forDynamic) = CtcGuesser.GuessReducerCtc(Ctc(2), Ctc(3), rules);

        Assert.Equal(3, forStatic.Value);  // fallback: dynamicCTC
        Assert.Equal(2, forDynamic.Value); // fallback: staticCTC
    }

    [Fact]
    public void GuessReducer_UndefinedStaticSameAsDynamic_BothSame()
    {
        var rules = Array.Empty<FittingMappingRule>();
        var (forStatic, forDynamic) = CtcGuesser.GuessReducerCtc(Ctc(0), Ctc(0), rules);
        Assert.Equal(0, forStatic.Value);
        Assert.Equal(0, forDynamic.Value);
    }
}
