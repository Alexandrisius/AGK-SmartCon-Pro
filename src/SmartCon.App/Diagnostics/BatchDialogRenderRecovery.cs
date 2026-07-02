#if NET48
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SmartCon.Core.Logging;

namespace SmartCon.App.Diagnostics;

/// <summary>
/// Production workaround for the "white WPF dialog" bug on Revit net48
/// (Issue #92). After <c>OpenDocumentFile</c> + family upgrade, Revit
/// internally kills the WPF render thread. <c>Window.ShowDialog()</c>
/// then shows a window with <c>WM_ERASEBKGND</c> (white) but no
/// <c>WM_PAINT</c> — the content area stays blank until the user
/// manually resizes the window.
///
/// This class fixes that by:
/// <list type="number">
///   <item>Positioning the window off-screen (Left = -20000) before
///         <c>ShowDialog</c> so the user never sees the white flash.</item>
///   <item>On <c>Loaded</c>, firing a <c>WM_ENTERSIZEMOVE</c> /
///         <c>SetWindowPos(+2px)</c> / <c>RedrawWindow(FORCE)</c> /
///         <c>WM_EXITSIZEMOVE</c> sequence — the closest programmatic
///         emulation of a manual resize that reliably wakes the render
///         thread (confirmed by WM_PAINT arriving in freeze-diagnostic.log).</item>
///   <item>When <c>WM_PAINT</c> arrives, moving the window to the centre
///         of the primary screen so the user sees a fully-rendered dialog.</item>
///   <item>If <c>WM_PAINT</c> never arrives within 500ms, moving the
///         window to the screen anyway so the user can manually resize
///         it as a fallback.</item>
/// </list>
///
/// Enabled on net48 only (Revit 2019–2024). Revit 2025+ runs on
/// net8.0-windows where the bug has not been observed.
/// </summary>
internal sealed class BatchDialogRenderRecovery : IDisposable
{
    private const int WM_PAINT = 0x000F;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;

    private const string Ctx = "RenderRecovery";

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private readonly Window _window;
    private readonly double _savedLeft;
    private readonly double _savedTop;
    private readonly WindowStartupLocation _savedStartup;
    private readonly HwndSourceHook _hook;
    private readonly DispatcherTimer _fallbackTimer;
    private bool _paintReceived;
    private bool _movedToScreen;
    private bool _kicked;
    private bool _disposed;

    private BatchDialogRenderRecovery(Window window)
    {
        _window = window;

        _savedLeft = window.Left;
        _savedTop = window.Top;
        _savedStartup = window.WindowStartupLocation;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;

        _hook = WndProcHook;
        _fallbackTimer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _fallbackTimer.Tick += OnFallbackTick;
    }

    public static BatchDialogRenderRecovery Attach(Window window)
    {
        var recovery = new BatchDialogRenderRecovery(window);

        recovery._window.SourceInitialized += recovery.OnSourceInitialized;
        recovery._window.Loaded += recovery.OnLoaded;
        recovery._window.ContentRendered += recovery.OnContentRendered;
        recovery._window.Closed += recovery.OnClosed;

        SmartConLogger.Freeze($"[{Ctx}] Attached: window placed off-screen (Left={recovery._window.Left}, Top={recovery._window.Top})");

        return recovery;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (PresentationSource.FromVisual(_window) is HwndSource source)
        {
            source.AddHook(_hook);
            SmartConLogger.Freeze($"[{Ctx}] HwndSource.AddHook OK hwnd={hwnd.ToInt64().ToString("X")}");
        }
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        SmartConLogger.Freeze($"[{Ctx}] Loaded — firing kick");
        KickRender();
        _fallbackTimer.Start();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        SmartConLogger.Freeze($"[{Ctx}] ContentRendered");
        if (!_paintReceived)
        {
            KickRender();
        }
        else
        {
            MoveToScreen("ContentRendered");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_PAINT && !_paintReceived)
        {
            _paintReceived = true;
            SmartConLogger.Freeze($"[{Ctx}] WM_PAINT received — render thread is alive");
            MoveToScreen("OnWmPaint");
        }
        return IntPtr.Zero;
    }

    private void KickRender()
    {
        if (_kicked) return;
        _kicked = true;

        try
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rc)) return;

            var width = rc.right - rc.left;
            var height = rc.bottom - rc.top;

            SmartConLogger.Freeze($"[{Ctx}] Kick: WM_ENTERSIZEMOVE → SetWindowPos(+2) → RedrawWindow → WM_EXITSIZEMOVE ({width}x{height})");

            SendMessage(hwnd, WM_ENTERSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width + 2, height,
                SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_UPDATENOW | RDW_ALLCHILDREN);
            SendMessage(hwnd, WM_EXITSIZEMOVE, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[{Ctx}] Kick FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void MoveToScreen(string reason)
    {
        if (_movedToScreen) return;
        _movedToScreen = true;
        _fallbackTimer.Stop();

        try
        {
            var workArea = SystemParameters.WorkArea;
            var left = workArea.Left + (workArea.Width - _window.ActualWidth) / 2;
            var top = workArea.Top + (workArea.Height - _window.ActualHeight) / 2;

            _window.Left = left;
            _window.Top = top;

            SmartConLogger.Freeze($"[{Ctx}] MoveToScreen ({reason}): left={left.ToString("F0")} top={top.ToString("F0")}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[{Ctx}] MoveToScreen ({reason}) FAILED: {ex.GetType().Name}: {ex.Message}");
            // last-resort fallback: restore saved coordinates
            _window.Left = _savedLeft;
            _window.Top = _savedTop;
        }
    }

    private void OnFallbackTick(object? sender, EventArgs e)
    {
        _fallbackTimer.Stop();
        if (!_movedToScreen)
        {
            SmartConLogger.Freeze($"[{Ctx}] Fallback 500ms: WM_PAINT not received, moving to screen anyway [Action: user may need to manually resize the window if still white]");
            MoveToScreen("Fallback500ms");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fallbackTimer.Stop();

        if (PresentationSource.FromVisual(_window) is HwndSource source)
        {
            try { source.RemoveHook(_hook); } catch { }
        }

        _window.SourceInitialized -= OnSourceInitialized;
        _window.Loaded -= OnLoaded;
        _window.ContentRendered -= OnContentRendered;
        _window.Closed -= OnClosed;
        _fallbackTimer.Tick -= OnFallbackTick;
    }
}
#endif
