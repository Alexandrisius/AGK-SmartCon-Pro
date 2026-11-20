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
        var args = Parse<RenameUnitArgs>(unit.ArgsJson);
        if (!TryFindByUniqueId<View>(
                context.Document, UniqueIdOf(unit, args), out var view, out var error))
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
        // A temp-only unit is verified right after its own apply (name == temp)
        // and again at job finalize, when the final unit has already renamed it.
        var verified = string.Equals(actual, expected, StringComparison.Ordinal)
                       || (args is { TempOnly: true }
                           && string.Equals(actual, args.NewName, StringComparison.Ordinal));
        return new JobUnitVerification
        {
            Verified = verified,
            ActualJson = Value(actual),
            MismatchReason = verified
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

        if (!TryFindByUniqueId<View>(
                context.Document,
                UniqueIdOf(unit, Parse<RenameUnitArgs>(unit.ArgsJson)),
                out var view,
                out var error))
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

        // Conflict guard: the job applied NewName; if the view now has a third
        // name, someone renamed it after the job and a silent overwrite would
        // lose that edit. Temp-only units are identified by their args.
        var args = Parse<RenameUnitArgs>(unit.ArgsJson);
        if (args is { TempOnly: false }
            && !string.IsNullOrWhiteSpace(args.NewName)
            && !string.Equals(current, args.NewName, StringComparison.Ordinal))
        {
            return new JobUnitUndo
            {
                ErrorCode = "undo_conflict",
                ErrorDetail = "The view is named '" + current + "' instead of the applied '" +
                              args.NewName + "'; it was renamed after the job. Restore it " +
                              "manually or rename it back before rolling back."
            };
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
