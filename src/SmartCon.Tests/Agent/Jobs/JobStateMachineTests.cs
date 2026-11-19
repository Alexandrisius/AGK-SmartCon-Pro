using SmartCon.Core.Models.Agent.Jobs;
using Xunit;

namespace SmartCon.Tests.Agent.Jobs;

public sealed class JobStateMachineTests
{
    [Theory]
    [InlineData(JobState.PreviewRunning, JobState.PreviewReady)]
    [InlineData(JobState.PreviewRunning, JobState.Failed)]
    [InlineData(JobState.PreviewRunning, JobState.Cancelled)]
    [InlineData(JobState.PreviewRunning, JobState.Interrupted)]
    [InlineData(JobState.PreviewReady, JobState.Running)]
    [InlineData(JobState.PreviewReady, JobState.Cancelled)]
    [InlineData(JobState.Running, JobState.Completed)]
    [InlineData(JobState.Running, JobState.CompletedWithFailures)]
    [InlineData(JobState.Running, JobState.Cancelling)]
    [InlineData(JobState.Running, JobState.Interrupted)]
    [InlineData(JobState.Cancelling, JobState.Cancelled)]
    [InlineData(JobState.Cancelling, JobState.RolledBack)]
    [InlineData(JobState.Interrupted, JobState.Running)]
    [InlineData(JobState.Completed, JobState.RolledBack)]
    [InlineData(JobState.CompletedWithFailures, JobState.Running)]
    [InlineData(JobState.Failed, JobState.Running)]
    [InlineData(JobState.RollbackPartial, JobState.RolledBack)]
    public void Allows_expected_transitions(JobState from, JobState to)
    {
        Assert.True(JobStateMachine.CanTransition(from, to));
        Assert.Null(JobStateMachine.Validate(from, to));
    }

    [Theory]
    [InlineData(JobState.PreviewRunning, JobState.Running)]
    [InlineData(JobState.PreviewRunning, JobState.Completed)]
    [InlineData(JobState.PreviewReady, JobState.Completed)]
    [InlineData(JobState.PreviewReady, JobState.PreviewReady)]
    [InlineData(JobState.Running, JobState.PreviewReady)]
    [InlineData(JobState.Running, JobState.PreviewRunning)]
    [InlineData(JobState.Completed, JobState.Running)]
    [InlineData(JobState.Completed, JobState.Cancelled)]
    [InlineData(JobState.Cancelled, JobState.Running)]
    [InlineData(JobState.RolledBack, JobState.Running)]
    public void Rejects_illegal_transitions(JobState from, JobState to)
    {
        Assert.False(JobStateMachine.CanTransition(from, to));

        var error = JobStateMachine.Validate(from, to);

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.InvalidState, error!.Code);
        Assert.Contains("[Action:", error.ToMessage());
    }

    [Fact]
    public void Self_transition_is_rejected_with_hint()
    {
        var error = JobStateMachine.Validate(JobState.Running, JobState.Running);

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.InvalidState, error!.Code);
        Assert.Contains("already", error.Message);
    }

    [Theory]
    [InlineData(JobState.Completed, true)]
    [InlineData(JobState.CompletedWithFailures, true)]
    [InlineData(JobState.Failed, true)]
    [InlineData(JobState.Cancelled, true)]
    [InlineData(JobState.RolledBack, true)]
    [InlineData(JobState.RollbackPartial, true)]
    [InlineData(JobState.Running, false)]
    [InlineData(JobState.PreviewRunning, false)]
    [InlineData(JobState.Cancelling, false)]
    [InlineData(JobState.Interrupted, false)]
    public void Classifies_terminal_states(JobState state, bool terminal)
    {
        Assert.Equal(terminal, JobStateMachine.IsTerminal(state));
    }

    [Theory]
    [InlineData(JobState.PreviewRunning, true)]
    [InlineData(JobState.Running, true)]
    [InlineData(JobState.Cancelling, true)]
    [InlineData(JobState.PreviewReady, false)]
    [InlineData(JobState.Interrupted, false)]
    [InlineData(JobState.Completed, false)]
    public void Classifies_states_that_need_the_pump(JobState state, bool needsPump)
    {
        Assert.Equal(needsPump, JobStateMachine.NeedsPump(state));
    }

    [Theory]
    [InlineData(JobState.PreviewReady, true)]
    [InlineData(JobState.Interrupted, true)]
    [InlineData(JobState.Running, true)]
    [InlineData(JobState.Completed, false)]
    [InlineData(JobState.Cancelled, false)]
    public void Classifies_document_lock(JobState state, bool locks)
    {
        Assert.Equal(locks, JobStateMachine.LocksDocument(state));
    }

    [Fact]
    public void Every_state_has_a_transition_entry()
    {
        foreach (JobState state in Enum.GetValues(typeof(JobState)))
        {
            Assert.NotNull(JobStateMachine.AllowedTargets(state));
        }
    }
}