using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

public sealed partial class RenameViewsJobAction
{
    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        if (!TryFindByUniqueId<View>(context.Document, unit.UnitKey, out var view, out var error))
        {
            return new JobUnitVerification
            {
                Verified = false,
                ErrorCode = error!.Code,
                ErrorDetail = error.Message,
                MismatchReason = "The view no longer exists"
            };
        }

        var expected = ReadValue(preview.ExpectedJson);
        var actual = view!.Name;
        return new JobUnitVerification
        {
            Verified = string.Equals(actual, expected, StringComparison.Ordinal),
            ActualJson = Value(actual),
            MismatchReason = string.Equals(actual, expected, StringComparison.Ordinal)
                ? null
                : "Expected name '" + expected + "', the view is named '" + actual + "'"
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
        {
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = "The unit has no recorded previous name"
            };
        }

        if (!TryFindByUniqueId<View>(context.Document, unit.UnitKey, out var view, out var error))
        {
            return new JobUnitUndo
            {
                ErrorCode = error!.Code,
                ErrorDetail = error.Message
            };
        }

        var current = view!.Name;
        if (string.Equals(current, target, StringComparison.Ordinal))
        {
            return new JobUnitUndo { Success = true, Skipped = true, ActualJson = Value(current) };
        }

        if (dryRun)
        {
            return new JobUnitUndo { Success = true, ActualJson = Value(current) };
        }

        try
        {
            view.Name = target;
            return new JobUnitUndo { Success = true, ActualJson = Value(target) };
        }
        catch (Exception ex)
        {
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackFailed,
                ErrorDetail = "Restoring '" + target + "' failed: " + ex.GetType().Name + ": " + ex.Message
            };
        }
    }

}
