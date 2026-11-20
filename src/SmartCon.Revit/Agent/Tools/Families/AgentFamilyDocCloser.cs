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
/// after enough cycles. Every step is best-effort and never throws.
/// </summary>
internal static class AgentFamilyDocCloser
{
    internal static void Close(Document? doc)
    {
        if (doc is null)
            return;

        try
        {
            doc.Close(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent family document could not be closed: " + ex.GetType().Name + ": " + ex.Message +
                " [Action: close the family document in Revit so it is not left open]");
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
    }
}
