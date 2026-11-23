using System.Text.Json;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Jobs.Actions;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Jobs;
using SmartCon.Revit.Agent.Tools.Write;
using Xunit;

namespace SmartCon.Tests.Agent.Jobs;

/// <summary>
/// Honest job messages (night run 2, C2-04/C2-05/C2-07): a failed preview is
/// never presented as applicable, failures aggregate by the code of the cause,
/// the action line follows the real cause and a family name that matched nothing
/// comes with the nearest loaded names.
/// </summary>
public sealed class JobMessageHonestyTests
{
    [Fact]
    public void PreviewReadyHint_AllUnitsFailed_SaysNothingToApply()
    {
        var job = Record(
            JobState.PreviewReady,
            Preview(count: 3, willChange: 0, failed: 3, digest: "d"));

        var hint = JobToolPayloads.PreviewReadyHint(job);

        Assert.StartsWith("nothing to apply: all 3 unit(s) failed the preview", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("dryRun=false", hint, StringComparison.Ordinal);
        Assert.Equal(hint, JobToolPayloads.NextHint(job));
    }

    [Fact]
    public void PreviewReadyHint_PartialFailure_StatesWhatIsApplied()
    {
        var job = Record(
            JobState.PreviewReady,
            Preview(count: 3, willChange: 2, failed: 1, digest: "abc"));

        var hint = JobToolPayloads.PreviewReadyHint(job);

        Assert.StartsWith("apply: call start_job with dryRun=false and previewDigest=abc", hint, StringComparison.Ordinal);
        Assert.Contains("Only the 2 unit(s) that passed the preview are applied", hint, StringComparison.Ordinal);
        Assert.Contains("1 failed unit(s) are reported as failed, not applied", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewReadyHint_CleanPreview_KeepsApplyHint()
    {
        var job = Record(
            JobState.PreviewReady,
            Preview(count: 2, willChange: 2, failed: 0, digest: "d"));

        Assert.Equal("apply: call start_job with dryRun=false and previewDigest=d", JobToolPayloads.PreviewReadyHint(job));
    }

    [Fact]
    public void NextHint_JobFailedByDocumentRefusal_TellsToMakeDocumentWritable()
    {
        var job = Record(JobState.Failed, null, JobErrorCodes.DocumentReadOnly);

        var hint = JobToolPayloads.NextHint(job);

        Assert.StartsWith("the project document refused edits", hint, StringComparison.Ordinal);
        Assert.Contains("start_job with dryRun=true again", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("apply: call start_job", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureCauses_GroupByCauseCode_NotByOuterItemFailed()
    {
        var items = new[]
        {
            Item(1, "permission_denied", "family A: read-only"),
            Item(2, "permission_denied", "family B: read-only"),
            Item(3, "family_mapping_invalid", "family C: no source")
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(JobToolPayloads.FailureCauses(items)));
        var causes = json.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, causes.Count);
        Assert.Equal("permission_denied", causes[0].GetProperty("errorCode").GetString());
        Assert.Equal(2, causes[0].GetProperty("count").GetInt32());
        Assert.Equal("family A", causes[0].GetProperty("exampleUnit").GetString());
        Assert.Equal("family_mapping_invalid", causes[1].GetProperty("errorCode").GetString());
        Assert.Equal(" Causes: permission_denied ×2, family_mapping_invalid ×1.", JobToolPayloads.CausesLine(items));
    }

    [Fact]
    public void FailureCauses_NoFailures_IsNullAndNoLine()
    {
        Assert.Null(JobToolPayloads.FailureCauses(null));
        Assert.Null(JobToolPayloads.FailureCauses(Array.Empty<JobItem>()));
        Assert.Equal(string.Empty, JobToolPayloads.CausesLine(null));
    }

    [Fact]
    public void Outcome_UnitError_CarriesCauseCodeAndConsistentAction()
    {
        var outcome = new WriteOutcome
        {
            Success = false,
            ErrorCode = "permission_denied",
            Message = "The project document is read-only, so the family 'F' cannot be edited."
        };

        Assert.Equal("permission_denied", FamilyJobOutcome.CodeOf(outcome));
        var text = FamilyJobOutcome.Describe(outcome);
        Assert.StartsWith("permission_denied: The project document is read-only", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "[Action: make the project accept edits again (close the Family Editor and other pending operations, " +
            "or reload the project; if the file is read-only, work in a writable copy), then run the preview again]",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("close the Family Editor for that family and retry", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Outcome_WithoutErrorCode_FallsBackToItemFailed()
    {
        var outcome = new WriteOutcome { Success = false, Message = "boom" };

        Assert.Equal(JobErrorCodes.ItemFailed, FamilyJobOutcome.CodeOf(outcome));
        Assert.StartsWith("job_item_failed: boom [Action: ", FamilyJobOutcome.Describe(outcome), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("permission_denied", "make the project accept edits again")]
    [InlineData("backup_failed", "check write access to the backup folder")]
    [InlineData("save_failed", "the backup is kept")]
    [InlineData("family_open_conflict", "close the Family Editor for that family")]
    [InlineData("unknown_code", "inspect the family with get_family_parameters")]
    public void Outcome_ActionFollowsRealCause(string code, string expected)
    {
        Assert.Contains(expected, FamilyJobOutcome.ActionFor(code), StringComparison.Ordinal);
    }

    [Fact]
    public void Nearest_TypoAndWordOrder_FindTheLoadedName()
    {
        var loaded = new[] { "Вентиль запорный", "Кран шаровый бронза", "Насос ЦН-40" };

        Assert.Equal(new[] { "Вентиль запорный" }, AgentNameSuggestions.Nearest("Вентиля запорный", loaded, 1));
        Assert.Contains("Кран шаровый бронза", AgentNameSuggestions.Nearest("Шаровый кран бронза", loaded));
    }

    [Fact]
    public void Nearest_NothingClose_IsEmptyAndAddsNoSuffix()
    {
        var loaded = new[] { "Вентиль запорный", "Кран шаровый бронза" };

        Assert.Empty(AgentNameSuggestions.Nearest("Термометр", loaded));
        Assert.Equal(string.Empty, AgentNameSuggestions.Suffix("Термометр", loaded));
        Assert.Equal(
            " Nearest loaded name(s): Вентиль запорный.",
            AgentNameSuggestions.Suffix("Вентиля запорный", loaded));
    }

    [Fact]
    public void ScheduleField_NotAvailableReason_ExplainsNonSharedFamilyParameter()
    {
        var reason = AgentScheduleWriteHelper.FieldNotAvailableReason("Заказчик");

        Assert.StartsWith("No schedulable field 'Заказчик'", reason, StringComparison.Ordinal);
        Assert.Contains("not shared is never a schedule field", reason, StringComparison.Ordinal);
        Assert.Contains("add_project_shared_parameter", reason, StringComparison.Ordinal);
        Assert.Contains("map_family_parameters", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void JobJson_KeepsCyrillicAsIs()
    {
        var json = JobJson.Serialize(new { name = "Кран шаровой", nested = new[] { "Насос" } });

        Assert.Contains("Кран шаровой", json, StringComparison.Ordinal);
        Assert.Contains("Насос", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u04", json, StringComparison.Ordinal);
    }

    private static JobPreview Preview(int count, int willChange, int failed, string digest) => new()
    {
        Action = "map_family_parameters",
        DocumentId = "doc",
        Count = count,
        WillChangeCount = willChange,
        FailedCount = failed,
        Digest = digest,
        SelectorHash = "s",
        SpecHash = "p"
    };

    private static JobRecord Record(JobState state, JobPreview? preview, string? errorCode = null) => new()
    {
        JobId = Guid.NewGuid(),
        Spec = new JobSpec
        {
            DocumentId = "doc",
            Action = "map_family_parameters",
            Mode = JobMode.Preview,
            SpecJson = "{}",
            Options = new JobOptions()
        },
        State = state,
        PreviewJson = preview is null ? null : JobJson.Serialize(preview),
        ErrorCode = errorCode
    };

    private static JobItem Item(int seq, string code, string detail) => new()
    {
        Seq = seq,
        UnitKey = "unit-" + seq,
        Label = "family " + (char)('A' + seq - 1),
        State = JobItemState.Failed,
        ErrorCode = code,
        ErrorDetail = detail
    };
}
