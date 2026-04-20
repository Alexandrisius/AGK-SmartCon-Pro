using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class PipeConnectStateTests
{
    private static readonly (PipeConnectState From, PipeConnectState To)[] ValidTransitions =
    [
        (PipeConnectState.AwaitingStaticSelection, PipeConnectState.AwaitingDynamicSelection),
        (PipeConnectState.AwaitingStaticSelection, PipeConnectState.Cancelled),
        (PipeConnectState.AwaitingDynamicSelection, PipeConnectState.AligningConnectors),
        (PipeConnectState.AwaitingDynamicSelection, PipeConnectState.Cancelled),
        (PipeConnectState.AligningConnectors, PipeConnectState.ResolvingParameters),
        (PipeConnectState.AligningConnectors, PipeConnectState.Cancelled),
        (PipeConnectState.ResolvingParameters, PipeConnectState.ResolvingFittings),
        (PipeConnectState.ResolvingParameters, PipeConnectState.Cancelled),
        (PipeConnectState.ResolvingFittings, PipeConnectState.PostProcessing),
        (PipeConnectState.ResolvingFittings, PipeConnectState.Cancelled),
        (PipeConnectState.PostProcessing, PipeConnectState.Committed),
        (PipeConnectState.PostProcessing, PipeConnectState.Cancelled),
    ];

    public static TheoryData<PipeConnectState, PipeConnectState> ValidTransitionData =>
        TheoryDataFromPairs(ValidTransitions);

    public static TheoryData<PipeConnectState, PipeConnectState> InvalidTransitionData =>
        ComputeInvalidTransitions();

    [Theory]
    [MemberData(nameof(ValidTransitionData))]
    public void CanTransition_ValidTransition_ReturnsTrue(PipeConnectState from, PipeConnectState to)
    {
        Assert.True(IsValidTransition(from, to), $"Expected {from} -> {to} to be valid");
    }

    [Theory]
    [MemberData(nameof(InvalidTransitionData))]
    public void CanTransition_InvalidTransition_ReturnsFalse(PipeConnectState from, PipeConnectState to)
    {
        Assert.False(IsValidTransition(from, to), $"Expected {from} -> {to} to be invalid");
    }

    [Fact]
    public void Cancelled_IsTerminal()
    {
        var states = Enum.GetValues<PipeConnectState>()
            .Where(s => s != PipeConnectState.Cancelled);

        foreach (var state in states)
        {
            Assert.False(IsValidTransition(PipeConnectState.Cancelled, state),
                $"Cancelled should not transition to {state}");
        }
    }

    [Fact]
    public void Committed_IsTerminal()
    {
        var states = Enum.GetValues<PipeConnectState>()
            .Where(s => s != PipeConnectState.Committed);

        foreach (var state in states)
        {
            Assert.False(IsValidTransition(PipeConnectState.Committed, state),
                $"Committed should not transition to {state}");
        }
    }

    [Fact]
    public void AllStates_AreCovered()
    {
        var defined = Enum.GetValues<PipeConnectState>();
        Assert.Equal(8, defined.Length);
    }

    private static bool IsValidTransition(PipeConnectState from, PipeConnectState to)
    {
        return ValidTransitions.Contains((from, to));
    }

    private static TheoryData<PipeConnectState, PipeConnectState> TheoryDataFromPairs(
        (PipeConnectState From, PipeConnectState To)[] pairs)
    {
        var data = new TheoryData<PipeConnectState, PipeConnectState>();
        foreach (var (from, to) in pairs)
            data.Add(from, to);
        return data;
    }

    private static TheoryData<PipeConnectState, PipeConnectState> ComputeInvalidTransitions()
    {
        var allStates = Enum.GetValues<PipeConnectState>();
        var validSet = new HashSet<(PipeConnectState, PipeConnectState)>(ValidTransitions);
        var data = new TheoryData<PipeConnectState, PipeConnectState>();

        foreach (var from in allStates)
        {
            foreach (var to in allStates)
            {
                if (from == to) continue;
                if (!validSet.Contains((from, to)))
                    data.Add(from, to);
            }
        }

        return data;
    }
}
