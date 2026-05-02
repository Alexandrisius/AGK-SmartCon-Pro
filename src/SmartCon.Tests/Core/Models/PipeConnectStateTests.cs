using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class PipeConnectStateTests
{
    [Theory]
    [InlineData(PipeConnectState.AwaitingStaticSelection, PipeConnectState.AwaitingDynamicSelection, true)]
    [InlineData(PipeConnectState.AwaitingStaticSelection, PipeConnectState.Cancelled, true)]
    [InlineData(PipeConnectState.AwaitingDynamicSelection, PipeConnectState.AligningConnectors, true)]
    [InlineData(PipeConnectState.AligningConnectors, PipeConnectState.ResolvingParameters, true)]
    [InlineData(PipeConnectState.ResolvingParameters, PipeConnectState.ResolvingFittings, true)]
    [InlineData(PipeConnectState.ResolvingFittings, PipeConnectState.PostProcessing, true)]
    [InlineData(PipeConnectState.PostProcessing, PipeConnectState.Committed, true)]
    [InlineData(PipeConnectState.PostProcessing, PipeConnectState.Cancelled, true)]
    [InlineData(PipeConnectState.AwaitingStaticSelection, PipeConnectState.Committed, false)]
    [InlineData(PipeConnectState.AwaitingDynamicSelection, PipeConnectState.Committed, false)]
    [InlineData(PipeConnectState.AligningConnectors, PipeConnectState.AwaitingStaticSelection, false)]
    [InlineData(PipeConnectState.ResolvingParameters, PipeConnectState.AligningConnectors, false)]
    [InlineData(PipeConnectState.ResolvingFittings, PipeConnectState.AwaitingDynamicSelection, false)]
    [InlineData(PipeConnectState.AwaitingDynamicSelection, PipeConnectState.ResolvingFittings, false)]
    public void CanTransition_ReturnsExpected(PipeConnectState from, PipeConnectState to, bool expected)
    {
        Assert.Equal(expected, PipeConnectStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void CanTransition_SameState_ReturnsTrue()
    {
        foreach (var state in Enum.GetValues<PipeConnectState>())
        {
            Assert.True(PipeConnectStateMachine.CanTransition(state, state),
                $"Same-state transition should be allowed for {state}");
        }
    }

    [Theory]
    [InlineData(PipeConnectState.Committed)]
    [InlineData(PipeConnectState.Cancelled)]
    public void IsTerminal_TerminalStates_ReturnTrue(PipeConnectState state)
    {
        Assert.True(PipeConnectStateMachine.IsTerminal(state));
    }

    [Theory]
    [InlineData(PipeConnectState.AwaitingStaticSelection)]
    [InlineData(PipeConnectState.AwaitingDynamicSelection)]
    [InlineData(PipeConnectState.AligningConnectors)]
    [InlineData(PipeConnectState.ResolvingParameters)]
    [InlineData(PipeConnectState.ResolvingFittings)]
    [InlineData(PipeConnectState.PostProcessing)]
    public void IsTerminal_NonTerminalStates_ReturnFalse(PipeConnectState state)
    {
        Assert.False(PipeConnectStateMachine.IsTerminal(state));
    }

    [Fact]
    public void Cancelled_CannotTransitionToAnyState()
    {
        foreach (var state in Enum.GetValues<PipeConnectState>())
        {
            if (state == PipeConnectState.Cancelled) continue;
            Assert.False(PipeConnectStateMachine.CanTransition(PipeConnectState.Cancelled, state),
                $"Cancelled should not transition to {state}");
        }
    }

    [Fact]
    public void Committed_CannotTransitionToAnyState()
    {
        foreach (var state in Enum.GetValues<PipeConnectState>())
        {
            if (state == PipeConnectState.Committed) continue;
            Assert.False(PipeConnectStateMachine.CanTransition(PipeConnectState.Committed, state),
                $"Committed should not transition to {state}");
        }
    }

    [Fact]
    public void AllStates_AreCovered()
    {
        var defined = Enum.GetValues<PipeConnectState>();
        Assert.Equal(8, defined.Length);
    }
}
