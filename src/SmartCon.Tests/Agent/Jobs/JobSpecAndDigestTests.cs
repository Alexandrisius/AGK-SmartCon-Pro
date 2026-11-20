using SmartCon.Core.Models.Agent.Jobs;
using Xunit;

namespace SmartCon.Tests.Agent.Jobs;

public sealed class JobSpecValidatorTests
{
    private static JobSpec Spec(
        JobMode mode = JobMode.Preview,
        string? digest = null,
        Guid? resumeId = null,
        JobOptions? options = null) =>
        new()
        {
            DocumentId = "doc-1",
            Action = "rename_views",
            Mode = mode,
            SpecJson = @"{""select"":{}}",
            PreviewDigest = digest,
            ResumeJobId = resumeId,
            Options = options ?? new JobOptions()
        };

    [Fact]
    public void Accepts_valid_preview_spec()
    {
        Assert.Null(JobSpecValidator.Validate(Spec()));
    }

    [Fact]
    public void Accepts_valid_apply_spec()
    {
        Assert.Null(JobSpecValidator.Validate(Spec(JobMode.Apply, digest: "abc")));
    }

    [Fact]
    public void Accepts_valid_resume_spec()
    {
        Assert.Null(JobSpecValidator.Validate(Spec(JobMode.Resume, resumeId: Guid.NewGuid())));
    }

    [Fact]
    public void Rejects_null_spec()
    {
        var error = JobSpecValidator.Validate(null);

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.InvalidSpec, error!.Code);
    }

    [Fact]
    public void Rejects_missing_document_id()
    {
        var spec = new JobSpec { DocumentId = " ", Action = "rename_views" };

        var error = JobSpecValidator.Validate(spec);

        Assert.NotNull(error);
        Assert.Contains("documentId", error!.Message);
    }

    [Fact]
    public void Rejects_apply_without_digest()
    {
        var error = JobSpecValidator.Validate(Spec(JobMode.Apply));

        Assert.NotNull(error);
        Assert.Contains("previewDigest", error!.Message);
    }

    [Fact]
    public void Rejects_preview_with_digest()
    {
        var error = JobSpecValidator.Validate(Spec(JobMode.Preview, digest: "abc"));

        Assert.NotNull(error);
        Assert.Contains("must not carry", error!.Message);
    }

    [Fact]
    public void Rejects_resume_without_job_id()
    {
        var error = JobSpecValidator.Validate(Spec(JobMode.Resume));

        Assert.NotNull(error);
        Assert.Contains("resumeJobId", error!.Message);
    }

    [Theory]
    [InlineData(0, 1500)]
    [InlineData(501, 1500)]
    [InlineData(50, 50)]
    [InlineData(50, 6000)]
    public void Rejects_out_of_range_options(int batchSize, int sliceBudget)
    {
        var options = new JobOptions { BatchSize = batchSize, SliceBudgetMs = sliceBudget };

        var error = JobSpecValidator.Validate(Spec(options: options));

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.InvalidSpec, error!.Code);
    }

    [Fact]
    public void Rejects_wrong_schema_version()
    {
        var spec = new JobSpec
        {
            SchemaVersion = 99,
            DocumentId = "doc",
            Action = "rename_views"
        };

        var error = JobSpecValidator.Validate(spec);

        Assert.NotNull(error);
        Assert.Contains("schemaVersion", error!.Message);
    }

    [Fact]
    public void Apply_envelope_accepts_matching_digest()
    {
        var spec = Spec(JobMode.Apply, digest: "digest-1");
        var preview = Preview("digest-1", expiresInMinutes: 10);

        Assert.Null(JobSpecValidator.ValidateApplyEnvelope(preview, spec, DateTime.UtcNow));
    }

    [Fact]
    public void Apply_envelope_rejects_digest_mismatch()
    {
        var spec = Spec(JobMode.Apply, digest: "other");
        var preview = Preview("digest-1", expiresInMinutes: 10);

        var error = JobSpecValidator.ValidateApplyEnvelope(preview, spec, DateTime.UtcNow);

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.PreviewMismatch, error!.Code);
    }

    [Fact]
    public void Apply_envelope_rejects_expired_preview()
    {
        var spec = Spec(JobMode.Apply, digest: "digest-1");
        var preview = Preview("digest-1", expiresInMinutes: -1);

        var error = JobSpecValidator.ValidateApplyEnvelope(preview, spec, DateTime.UtcNow);

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.PreviewExpired, error!.Code);
    }

    [Fact]
    public void Apply_envelope_rejects_document_mismatch()
    {
        var spec = Spec(JobMode.Apply, digest: "digest-1");
        var preview = new JobPreview
        {
            Action = spec.Action,
            DocumentId = "other-doc",
            Count = 1,
            SelectorHash = "s",
            SpecHash = "h",
            Digest = "digest-1",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        };

        var error = JobSpecValidator.ValidateApplyEnvelope(preview, spec, DateTime.UtcNow);

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.DocumentMismatch, error!.Code);
    }

    [Fact]
    public void Apply_envelope_rejects_action_mismatch()
    {
        var spec = Spec(JobMode.Apply, digest: "digest-1");
        var preview = new JobPreview
        {
            Action = "set_parameters",
            DocumentId = spec.DocumentId,
            Count = 1,
            SelectorHash = "s",
            SpecHash = "h",
            Digest = "digest-1",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        };

        var error = JobSpecValidator.ValidateApplyEnvelope(preview, spec, DateTime.UtcNow);

        Assert.NotNull(error);
        Assert.Equal(JobErrorCodes.PreviewMismatch, error!.Code);
    }

    private static JobPreview Preview(string digest, int expiresInMinutes) =>
        new()
        {
            Action = "rename_views",
            DocumentId = "doc-1",
            Count = 3,
            WillChangeCount = 2,
            SkipCount = 1,
            SelectorHash = "selector",
            SpecHash = "spec",
            Digest = digest,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(expiresInMinutes)
        };
}

public sealed class JobPreviewDigestTests
{
    private static readonly DateTime Expiry = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Digest_is_deterministic()
    {
        var first = JobPreviewDigest.Compute("rename_views", "doc", "sel", "spec", 10, 8, Expiry);
        var second = JobPreviewDigest.Compute("rename_views", "doc", "sel", "spec", 10, 8, Expiry);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Theory]
    [InlineData("rename_views", "doc2", "sel", "spec", 10, 8)]
    [InlineData("rename_views", "doc", "sel2", "spec", 10, 8)]
    [InlineData("rename_views", "doc", "sel", "spec2", 10, 8)]
    [InlineData("rename_views", "doc", "sel", "spec", 11, 8)]
    [InlineData("rename_views", "doc", "sel", "spec", 10, 9)]
    [InlineData("set_parameters", "doc", "sel", "spec", 10, 8)]
    public void Every_component_changes_the_digest(
        string action,
        string documentId,
        string selector,
        string spec,
        int count,
        int willChange)
    {
        var baseline = JobPreviewDigest.Compute("rename_views", "doc", "sel", "spec", 10, 8, Expiry);
        var other = JobPreviewDigest.Compute(action, documentId, selector, spec, count, willChange, Expiry);

        Assert.NotEqual(baseline, other);
    }

    [Fact]
    public void Expiry_changes_the_digest()
    {
        var first = JobPreviewDigest.Compute("a", "d", "s", "p", 1, 1, Expiry);
        var second = JobPreviewDigest.Compute("a", "d", "s", "p", 1, 1, Expiry.AddMinutes(1));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Selector_hash_is_order_independent()
    {
        var units = new[]
        {
            new JobUnit { Seq = 1, UnitKey = "a", ArgsJson = @"{""v"":1}" },
            new JobUnit { Seq = 2, UnitKey = "b", ArgsJson = @"{""v"":2}" }
        };
        var reversed = new[] { units[1], units[0] };

        Assert.Equal(
            JobPreviewDigest.ComputeSelectorHash(units),
            JobPreviewDigest.ComputeSelectorHash(reversed));
    }

    [Fact]
    public void Selector_hash_detects_arg_change()
    {
        var original = new[]
        {
            new JobUnit { Seq = 1, UnitKey = "a", ArgsJson = @"{""name"":""A""}" }
        };
        var changed = new[]
        {
            new JobUnit { Seq = 1, UnitKey = "a", ArgsJson = @"{""name"":""B""}" }
        };

        Assert.NotEqual(
            JobPreviewDigest.ComputeSelectorHash(original),
            JobPreviewDigest.ComputeSelectorHash(changed));
    }

    [Fact]
    public void Totals_are_computed_from_items()
    {
        var items = new[]
        {
            Item(JobItemState.Done),
            Item(JobItemState.Done),
            Item(JobItemState.Skipped),
            Item(JobItemState.Failed),
            Item(JobItemState.Mismatch),
            Item(JobItemState.Pending),
            Item(JobItemState.Undone),
            Item(JobItemState.UndoFailed),
            Item(JobItemState.Undoing),
            Item(JobItemState.Applying)
        };

        var totals = JobTotals.FromItems(items);

        Assert.Equal(10, totals.Total);
        Assert.Equal(2, totals.Done);
        // Pending/Applying/Undoing/Undone are all "not finished": a job with any of
        // them must never be reported as Completed.
        Assert.Equal(4, totals.Incomplete);
        Assert.Equal(1, totals.Skipped);
        Assert.Equal(1, totals.Failed);
        Assert.Equal(1, totals.Mismatch);
        Assert.NotNull(totals);
    }

    [Fact]
    public void Save_policy_decisions_follow_the_policy()
    {
        Assert.True(JobSavePolicyDecider.ShouldSaveAfterBatch(JobSavePolicy.AfterEachBatch, true));
        Assert.False(JobSavePolicyDecider.ShouldSaveAfterBatch(JobSavePolicy.AfterEachBatch, false));
        Assert.False(JobSavePolicyDecider.ShouldSaveAfterBatch(JobSavePolicy.Never, true));

        var now = DateTime.UtcNow;
        Assert.True(JobSavePolicyDecider.ShouldSaveOnTimer(
            JobSavePolicy.EveryNMinutes, now.AddMinutes(-6), now, 5));
        Assert.False(JobSavePolicyDecider.ShouldSaveOnTimer(
            JobSavePolicy.EveryNMinutes, now.AddMinutes(-2), now, 5));
        Assert.False(JobSavePolicyDecider.ShouldSaveOnTimer(
            JobSavePolicy.AfterEachBatch, now.AddMinutes(-60), now, 5));

        Assert.True(JobSavePolicyDecider.RequiresSaveableDocument(JobSavePolicy.AfterEachBatch));
        Assert.False(JobSavePolicyDecider.RequiresSaveableDocument(JobSavePolicy.Never));
    }

    private static JobItem Item(JobItemState state) => new()
    {
        Seq = 1,
        UnitKey = "unit",
        State = state
    };
}