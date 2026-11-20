using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

public sealed partial class SetParametersJobAction
{
    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        if (!TryPrepare(context, unit, out var args, out var parameter, out var code, out var detail))
            return VerifyFail(code, detail, detail);
        var expectedRaw = ReadValue(preview.ExpectedJson);
        if (expectedRaw is null)
            return VerifyFail(JobErrorCodes.InvalidSpec, "The preview has no expected value",
                "The preview has no expected value " + Hint("run the preview again"));
        var actualRaw = AgentParameterWriteHelper.RawValue(parameter);
        var verified = RawMatches(parameter.StorageType, actualRaw, expectedRaw);
        return new JobUnitVerification
        {
            Verified = verified,
            ActualJson = Value(actualRaw),
            MismatchReason = verified
                ? null
                : "Parameter '" + NameOf(parameter, args) + "' holds '" + actualRaw + "', the preview expected '" +
                  expectedRaw + "' " + Hint("run get_job_report, then rollback or retry with a fresh preview")
        };
    }

    public override JobUnitUndo Undo(
        JobActionContext context,
        JobUnit unit,
        string? undoJson,
        bool dryRun)
    {
        var target = ReadValue(undoJson);
        if (target is null)
            return UndoFail(JobErrorCodes.RollbackUnavailable, "No recorded previous value " +
                Hint("restore this value from a backup or manually"));
        if (!TryPrepare(context, unit, out var args, out var parameter, out _, out var detail))
            return UndoFail(JobErrorCodes.RollbackFailed, detail + " " + Hint("restore this value manually"));
        var currentRaw = AgentParameterWriteHelper.RawValue(parameter);
        if (RawMatches(parameter.StorageType, currentRaw, target))
            return new JobUnitUndo { Success = true, Skipped = true, ActualJson = Value(currentRaw) };
        if (dryRun)
            return new JobUnitUndo { Success = true, ActualJson = Value(currentRaw) };
        try
        {
            if (!AgentParameterWriteHelper.TrySet(parameter, target, "raw", out _, out var reason))
            {
                return UndoFail(JobErrorCodes.RollbackFailed, "Restoring '" + target + "' on parameter '" +
                    NameOf(parameter, args) + "' failed: " + reason + " " + Hint("restore this value manually"));
            }
        }
        catch (Exception ex)
        {
            return UndoFail(JobErrorCodes.RollbackFailed, "Restoring '" + target + "' failed: " +
                ex.GetType().Name + ": " + ex.Message + " " + Hint("restore this value manually"));
        }
        return new JobUnitUndo { Success = true, ActualJson = Value(target) };
    }

    private static bool TryPrepare(
        JobActionContext context,
        JobUnit unit,
        [NotNullWhen(true)] out SetParameterUnitArgs? args,
        [NotNullWhen(true)] out Parameter? parameter,
        out string errorCode,
        out string errorDetail)
    {
        args = Parse<SetParameterUnitArgs>(unit.ArgsJson);
        parameter = null;
        errorCode = JobErrorCodes.InvalidSpec;
        errorDetail = "Unit arguments are unreadable " + Hint("run the preview again");
        if (args is null)
            return false;
        if (!string.IsNullOrWhiteSpace(args.Error))
        {
            errorCode = args.ErrorCode ?? ParameterNotFound;
            errorDetail = args.Error!;
            return false;
        }
        var uniqueId = string.IsNullOrWhiteSpace(args.OwnerUniqueId) ? args.UniqueId : args.OwnerUniqueId;
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            errorDetail = "The unit has no element UniqueId " + Hint("run the preview again");
            return false;
        }
        if (!TryFindByUniqueId<Element>(context.Document, uniqueId!, out var element, out var findError))
        {
            errorCode = findError!.Code;
            errorDetail = findError.ToMessage();
            return false;
        }
        var resolution = AgentParameterWriteHelper.Resolve(
            context.Document, element!,
            string.IsNullOrWhiteSpace(args.BuiltInParameter) ? args.Parameter : args.BuiltInParameter,
            args.Guid, args.Scope);
        if (!resolution.Ok)
        {
            errorCode = resolution.ErrorCode ?? ParameterNotFound;
            errorDetail = (resolution.Reason ?? "The parameter could not be resolved") + " " + ResolutionHint;
            return false;
        }
        parameter = resolution.Parameter!;
        errorCode = errorDetail = string.Empty;
        return true;
    }

    // Preview must not touch the model, so the post-write raw value is computed with the
    // same unit table AgentParameterWriteHelper.TrySet uses; Verify compares RawValue to it.
}
