namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Platform-specific workaround that forces the WPF/render thread to re-sync
/// with the UI thread. On Revit net48 this is typically implemented by
/// triggering an InfoCenter balloon, whose Win32 window creation produces the
/// focus flip needed to recover from the well-known WPF render-thread zombie
/// state (REVIT-236376 / REVIT-237190).
/// </summary>
public interface IUiFreezeRecoveryService
{
    /// <summary>
    /// Performs a lightweight recovery nudge. A <paramref name="message"/> of
    /// <c>" "</c> (single space) or another non-user-facing text should be used
    /// when the nudge is invisible; a real message is shown when the caller
    /// wants a visible balloon.
    /// </summary>
    void Nudge(string message);
}
