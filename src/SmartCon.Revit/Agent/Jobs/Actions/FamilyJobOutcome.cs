using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Turns a family-session outcome into the unit error of a family job. The code of
/// the CAUSE is the unit's errorCode (not the outer job_item_failed), so failures
/// aggregate by cause, the job runner recognises a document-level refusal and the
/// action line follows the real cause instead of one fixed sentence.
/// </summary>
internal static class FamilyJobOutcome
{
    internal static string CodeOf(WriteOutcome outcome) =>
        outcome.ErrorCode ?? JobErrorCodes.ItemFailed;

    internal static string Describe(WriteOutcome outcome) =>
        CodeOf(outcome) + ": " +
        (outcome.Message ?? "The family document could not be prepared.") +
        " [Action: " + ActionFor(outcome.ErrorCode) + "]";

    internal static string ActionFor(string? errorCode)
    {
        if (string.Equals(errorCode, AgentFamilySession.PermissionDenied, StringComparison.Ordinal))
        {
            return "make the project accept edits again (close the Family Editor and other pending " +
                   "operations, or reload the project; if the file is read-only, work in a writable " +
                   "copy), then run the preview again";
        }

        return errorCode switch
        {
            "in_place_not_supported" => "edit the in-place family manually in the Family Editor",
            "family_not_editable" => "pick a loadable, editable family with list_families",
            "family_open_conflict" => "close the Family Editor for that family and run the preview again",
            "family_open_failed" =>
                "close the Family Editor and run the preview again; check the family manually if it repeats",
            "backup_failed" => "check write access to the backup folder and run the job again",
            "save_failed" => "close other family operations and run the job again; the backup is kept",
            "read_back_mismatch" => "inspect the family in the Family Editor or undo the job",
            _ => "inspect the family with get_family_parameters and run the preview again"
        };
    }
}
