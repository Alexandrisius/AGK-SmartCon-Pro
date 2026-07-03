using SmartCon.Core.Logging;

namespace SmartCon.Revit.Util;

/// <summary>
/// Freeze workaround for the well-known Revit + WPF DockablePane freeze on
/// R2023 / R2025 net48 after the family upgrade dialog closes
/// (REVIT-236376 / REVIT-237190). User-side recovery is to right-click on
/// the pane, which forces a Win32 focus event and re-syncs the WPF render
/// thread with the UI thread.
///
/// We emulate that nudge by triggering an InfoCenter balloon tip via
/// AdWindows.dll — the same workaround confirmed by the Revit API forum
/// thread "Loading a rfa file into a document using LoadFamily() freezes
/// Revit UI" (Fausto Mendez, 2021): the balloon's own Win32 window forces
/// the focus flip that wakes the render thread.
///
/// AdWindows.dll lives next to RevitAPI in the Revit install folder; it is
/// not an officially supported Revit API, but it has been stable since at
/// least Revit 2016 and is used by every popular Revit plugin that needs
/// to surface information to the user.
///
/// Every step is best-effort and never throws — the call chain we are
/// trying to fix is already broken, so adding a fault here would only
/// hide the real error.
///
/// v2.0.0 build-time resilience: the AdWindows-using body is wrapped in
/// <c>#if !NO_ADWINDOWS</c>. <c>NO_ADWINDOWS</c> is defined by
/// SmartCon.Revit.csproj when no <c>AdWindows.dll</c> was found at any
/// expected HintPath on the build machine. On such machines
/// <see cref="Nudge"/> compiles to a no-op (still logs once at Debug so
/// the missing workaround is visible in the log) — the rest of
/// SmartCon is unaffected because <see cref="Nudge"/> is best-effort.
/// </summary>
public static class RevitBalloonNudge
{
    private static bool _warnedOnce;

    /// <summary>
    /// Shows a near-invisible InfoCenter balloon for the shortest possible
    /// time. The balloon's Win32 window forces the focus event that wakes
    /// the WPF render thread after a family upgrade dialog.
    /// </summary>
    public static void Nudge(string message)
    {
        SmartConLogger.Freeze($"RevitBalloonNudge.Nudge invoked: message='{message}'");

#if NO_ADWINDOWS
        if (_warnedOnce) return;
        _warnedOnce = true;
        SmartConLogger.Freeze(
            "RevitBalloonNudge skipped: AdWindows.dll was not found on this build machine. " +
            "The family-upgrade freeze workaround is unavailable; " +
            "right-click on the DockablePane will still recover the UI.");
#else
        try
        {
            var palette = Autodesk.Windows.ComponentManager.InfoCenterPaletteManager;
            if (palette is null) return;

            var ri = new Autodesk.Internal.InfoCenter.ResultItem
            {
                Title = message,
                IsFavorite = false,
                IsNew = false,
            };

            // Shortest possible display period — we want the focus event,
            // not the user-visible notification.
            var config = palette.Configuration;
            config.BalloonDisplayPeriod = 1;

            // 80 = barely visible; the user should never notice this fired.
            config.BalloonTransparency = 80;

            palette.ShowBalloon(ri);
        }
        catch (Exception ex)
        {
            if (_warnedOnce) return;
            _warnedOnce = true;
            SmartConLogger.Warn(
                $"RevitBalloonNudge failed: {ex.Message} " +
                "[Action: balloon workaround unavailable — right-click on the DockablePane will still recover the UI]");
        }
#endif
    }
}
