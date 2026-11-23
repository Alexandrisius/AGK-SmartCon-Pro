using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Close routine for background family documents, copied from the FamilyManager
/// batch extractors (issues #96 / REVIT-237190): after every OpenDocumentFile/
/// EditFamily + Close cycle the temporary document is released synchronously
/// (bypassing the finalizer) and a near-invisible InfoCenter balloon forces the
/// Win32 focus event that re-syncs the WPF render thread with the UI thread —
/// without it a DockablePane (the agent pane) freezes non-deterministically
/// after enough cycles. The cleanup steps are best-effort and never throw;
/// the close itself is reported, never assumed.
/// </summary>
internal static class AgentFamilyDocCloser
{
    /// <summary>
    /// Closes the document and returns true only when it is really gone (the
    /// Revit wrapper is invalid afterwards). A family document that survives
    /// Close keeps its project read-only, so a false result must reach the
    /// caller instead of a silent "closed".
    /// </summary>
    internal static bool Close(Document? doc)
    {
        if (doc is null)
            return true;

        string? failure = null;
        try
        {
            doc.Close(false);
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
        }

        // Read before the COM release: afterwards the wrapper may be unusable.
        bool closed;
        try
        {
            closed = !doc.IsValidObject;
        }
        catch (Exception)
        {
            closed = failure is null;
        }

        if (!closed)
        {
            SmartConLogger.Warn(
                "Agent family document '" + TitleOf(doc) + "' is still open after Close(false)" +
                (failure is null ? string.Empty : ": " + failure) +
                "; the project document stays read-only until it is closed " +
                "[Action: close that family document in Revit (or reload the project) before the " +
                "next family operation]");
        }

        // REVIT-237190: force synchronous COM cleanup of the temporary document.
        // On Revit versions where Document is a managed wrapper this throws
        // ArgumentException — ignored.
        try
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(doc);
        }
        catch (Exception)
        {
            // Managed wrapper, not a real COM object.
        }

        // Workaround #96: invisible balloon forces the WPF/render-thread resync.
        try
        {
            RevitBalloonNudge.Nudge(" ");
        }
        catch (Exception)
        {
            // Best-effort: right-click on the pane still recovers the UI.
        }

        return closed;
    }

    private static string TitleOf(Document doc)
    {
        try
        {
            return doc.Title;
        }
        catch (Exception)
        {
            return "(unknown)";
        }
    }
}
