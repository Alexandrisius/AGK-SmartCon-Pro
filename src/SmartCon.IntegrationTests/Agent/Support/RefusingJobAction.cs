using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.IntegrationTests.Agent.Support;

/// <summary>
/// Job action double for the job runner's document-refusal path: every unit
/// previews and applies fine except the one at <see cref="RefuseAtSeq"/>, which is
/// refused the way a family unit is refused by a read-only project. No Revit API
/// is touched (the runner only needs a document for its identity and state).
/// </summary>
internal sealed class RefusingJobAction : IJobAction
{
    public const string RefusalCode = "permission_denied";

    public int UnitCount { get; init; } = 4;

    public int RefuseAtSeq { get; init; } = 2;

    public string Name => "refusing_probe";

    public JobActionScope Scope => JobActionScope.FamilyDefinition;

    public int MaxUnits => 200;

    public JobExpansion Expand(JobActionContext context, string specJson) => new()
    {
        Units = Enumerable.Range(1, UnitCount)
            .Select(seq => new JobUnit
            {
                Seq = 0,
                UnitKey = "unit-" + seq,
                Label = "Unit " + seq,
                ArgsJson = "{}"
            })
            .ToList()
    };

    public JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        if (unit.Seq != RefuseAtSeq)
        {
            return new JobUnitPreview { ExpectedJson = "{}", ActualJson = "{}", UndoJson = "{}", Label = unit.Label };
        }

        return new JobUnitPreview
        {
            Label = unit.Label,
            ErrorCode = RefusalCode,
            ErrorDetail = RefusalCode + ": The project document is read-only, so the family '" +
                          unit.Label + "' cannot be edited. [Action: make the project accept edits again]"
        };
    }

    public JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo)
    {
        if (unit.Seq != RefuseAtSeq)
        {
            return new JobUnitApply { Success = true, ExpectedJson = "{}", UndoJson = "{}" };
        }

        return new JobUnitApply { ErrorCode = RefusalCode, ErrorDetail = "refused " + unit.Label };
    }

    public JobUnitVerification Verify(JobActionContext context, JobUnit unit, JobUnitPreview preview) =>
        new() { Verified = true, ActualJson = "{}" };

    public JobUnitUndo Undo(JobActionContext context, JobUnit unit, string? undoJson, bool dryRun) =>
        new() { Success = true, ActualJson = "{}" };
}
