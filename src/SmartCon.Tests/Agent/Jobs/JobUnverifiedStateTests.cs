using SmartCon.Core.Models.Agent.Jobs;
using Xunit;

namespace SmartCon.Tests.Agent.Jobs;

/// <summary>
/// B.6 (#432): targets that no project-side read can verify (family instance
/// scope / element references) are "unknown", not "wrong" — the job settles as
/// CompletedUnverified instead of poisoning the result with mismatches.
/// </summary>
public sealed class JobUnverifiedStateTests
{
    [Fact]
    public void JobState_IsAppendOnly_CompletedUnverifiedIsLast()
    {
        // Persisted as the integer value in SQLite: existing numbers must never
        // be reused and new states are appended at the end only.
        Assert.Equal(13, Enum.GetValues(typeof(JobState)).Length);
        Assert.Equal(12, (int)JobState.CompletedUnverified);

        var expected = new[]
        {
            "PreviewRunning", "PreviewReady", "AwaitingApproval", "Running", "Completed",
            "CompletedWithFailures", "Failed", "Cancelling", "Cancelled", "Interrupted",
            "RolledBack", "RollbackPartial", "CompletedUnverified"
        };
        var names = Enum.GetNames(typeof(JobState));
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], names[i]);
            Assert.Equal(i, (int)Enum.Parse(typeof(JobState), names[i]));
        }

        Assert.Equal("completed_unverified", JobEventTypes.CompletedUnverified);
    }

    [Fact]
    public void Decide_ZeroFailedWithNotVerifiableOnly_CompletedUnverified()
    {
        var state = JobFinalStateDecider.Decide(new JobTotals
        {
            Total = 4,
            Done = 4,
            NotVerifiable = 4
        });

        Assert.Equal(JobState.CompletedUnverified, state);
    }

    [Fact]
    public void Decide_AllZeroClean_Completed()
    {
        var state = JobFinalStateDecider.Decide(new JobTotals { Total = 2, Done = 2 });

        Assert.Equal(JobState.Completed, state);
    }

    [Theory]
    [InlineData(1, 0, 0, 0)] // real mismatch wins
    [InlineData(0, 1, 0, 0)] // failed wins
    [InlineData(0, 0, 1, 0)] // undoFailed wins
    [InlineData(0, 0, 0, 1)] // incomplete wins
    public void Decide_RealFailuresBeatNotVerifiable_CompletedWithFailures(
        int mismatch,
        int failed,
        int undoFailed,
        int incomplete)
    {
        var state = JobFinalStateDecider.Decide(new JobTotals
        {
            Total = 4,
            Done = 3,
            Mismatch = mismatch,
            Failed = failed,
            UndoFailed = undoFailed,
            Incomplete = incomplete,
            NotVerifiable = 3
        });

        Assert.Equal(JobState.CompletedWithFailures, state);
    }

    [Fact]
    public void FromItems_DoneUnitsWithNotVerifiable_SummedSeparatelyFromMismatch()
    {
        var totals = JobTotals.FromItems(new[]
        {
            Item(JobItemState.Done, notVerifiable: 4),
            Item(JobItemState.Done, notVerifiable: 0),
            Item(JobItemState.Skipped)
        });

        Assert.Equal(2, totals.Done);
        Assert.Equal(1, totals.Skipped);
        Assert.Equal(4, totals.NotVerifiable);
        Assert.Equal(0, totals.Mismatch);
        Assert.Equal(0, totals.Failed);
    }

    [Fact]
    public void FromItems_RealMismatchAndUnverifiable_AreCountedIndependently()
    {
        // 1 real mismatch + 3 unverifiable targets must not swallow each other.
        var totals = JobTotals.FromItems(new[]
        {
            Item(JobItemState.Mismatch),
            Item(JobItemState.Done, notVerifiable: 3)
        });

        Assert.Equal(1, totals.Mismatch);
        Assert.Equal(3, totals.NotVerifiable);
        Assert.Equal(JobState.CompletedWithFailures, JobFinalStateDecider.Decide(totals));
    }

    [Fact]
    public void FromItems_SkippedUnitWithNotVerifiable_AlsoCounts()
    {
        var totals = JobTotals.FromItems(new[]
        {
            Item(JobItemState.Skipped, notVerifiable: 2)
        });

        Assert.Equal(1, totals.Skipped);
        Assert.Equal(2, totals.NotVerifiable);
        Assert.Equal(JobState.CompletedUnverified, JobFinalStateDecider.Decide(totals));
    }

    [Fact]
    public void TotalsJson_RoundTripKeepsNotVerifiable()
    {
        var json = JobJson.Serialize(new JobTotals { Total = 1, Done = 1, NotVerifiable = 7 });

        Assert.Contains("\"notVerifiable\":7", json);

        var parsed = JobJson.Deserialize<JobTotals>(json);
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!.NotVerifiable);

        // Pre-v6 totals JSON (no field) still reads as "nothing unverifiable".
        var legacy = JobJson.Deserialize<JobTotals>("{\"total\":1,\"done\":1}");
        Assert.NotNull(legacy);
        Assert.Equal(0, legacy!.NotVerifiable);
    }

    [Fact]
    public void Verification_NotVerifiableWithVerified_DoesNotReadAsMismatch()
    {
        var verification = new JobUnitVerification { Verified = true, NotVerifiable = 4 };

        Assert.True(verification.Verified);
        Assert.Equal(4, verification.NotVerifiable);
        Assert.Null(verification.MismatchReason);
    }

    private static JobItem Item(JobItemState state, int notVerifiable = 0) => new()
    {
        Seq = 1,
        UnitKey = "unit-" + state,
        State = state,
        NotVerifiable = notVerifiable
    };
}
