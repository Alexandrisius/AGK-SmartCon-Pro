using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SmartCon.Core.Logging;

namespace SmartCon.App.Diagnostics;

/// <summary>
/// Debug-only diagnostics for the "white WPF dialog" freeze (Issue #92).
/// Attaches Win32 message hooks and WPF lifecycle event listeners to a modal
/// window and writes every observable signal into
/// <c>freeze-diagnostic.log</c>. The goal is to catch the exact moment the
/// WPF render thread stops delivering WM_PAINT after WM_ERASEBKGND.
///
/// No recovery logic lives here — that is handled by
/// <see cref="BatchDialogRenderRecovery"/> (net48 only). This class is
/// purely observational: it logs so we can confirm in the freeze log that
/// WM_PAINT arrived after the kick.
///
/// Enabled only in Debug builds so Release users pay zero overhead.
/// </summary>
internal static class WhiteDialogDiagnostics
{
    private const int WM_NULL = 0x0000;
    private const int WM_CREATE = 0x0001;
    private const int WM_DESTROY = 0x0002;
    private const int WM_MOVE = 0x0003;
    private const int WM_SIZE = 0x0005;
    private const int WM_ACTIVATE = 0x0006;
    private const int WM_SETFOCUS = 0x0007;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_ACTIVATEAPP = 0x001C;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCPAINT = 0x0085;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int WM_TIMER = 0x0113;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const int WM_DWMNCRENDERINGCHANGED = 0x031F;

    private static readonly ConditionalWeakTable<Window, HwndSourceHook> ActiveHooks = new();
    private static long _postedOperations;
    private static long _completedOperations;
    private static bool _dispatcherHooksInstalled;
    private static bool _tierChangedSubscribed;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>
    /// Attaches white-dialog diagnostics to <paramref name="window"/>.
    /// No-op in Release builds.
    /// </summary>
    public static void Attach(Window window, string context)
    {
#if DEBUG
        if (window is null)
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Attach skipped: window is null");
            return;
        }

        try
        {
            InstallDispatcherHooksOnce(window.Dispatcher, context);
            SubscribeWindowEvents(window, context);
            AttachHwndSourceHook(window, context);
            LogSnapshot(window, context, "Attach");
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Attach THREW: {ex.GetType().Name}: {ex.Message}");
        }
#else
        _ = window;
        _ = context;
#endif
    }

#if DEBUG
    private static void InstallDispatcherHooksOnce(Dispatcher dispatcher, string context)
    {
        if (_dispatcherHooksInstalled)
            return;

        try
        {
            dispatcher.Hooks.OperationPosted += (_, _) => Interlocked.Increment(ref _postedOperations);
            dispatcher.Hooks.OperationCompleted += (_, _) => Interlocked.Increment(ref _completedOperations);
            _dispatcherHooksInstalled = true;
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Dispatcher.Hooks installed (thread={dispatcher.Thread.ManagedThreadId})");
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Dispatcher.Hooks install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SubscribeWindowEvents(Window window, string context)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwndSource = PresentationSource.FromVisual(window) as HwndSource;
            SmartConLogger.Freeze(
                $"[WhiteDialog:{context}] SourceInitialized hwnd=" +
                $"{(hwndSource?.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture) ?? "null")}");
            AttachHwndSourceHook(window, context);
        };

        window.Loaded += (_, _) =>
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Loaded");
            LogSnapshot(window, context, "Loaded");
        };

        window.ContentRendered += (_, _) =>
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] ContentRendered — paint reached this window");
            LogSnapshot(window, context, "ContentRendered");
        };

        window.Activated += (_, _) =>
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Activated");
            LogSnapshot(window, context, "Activated");
        };

        window.Deactivated += (_, _) =>
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Deactivated");
            LogSnapshot(window, context, "Deactivated");
        };

        window.SizeChanged += (_, e) =>
        {
            SmartConLogger.Freeze(
                $"[WhiteDialog:{context}] SizeChanged " +
                $"newSize={e.NewSize.Width.ToString("F1", CultureInfo.InvariantCulture)}x" +
                $"{e.NewSize.Height.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"reason={e.WidthChanged},{e.HeightChanged}");
        };

        window.StateChanged += (_, _) =>
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] StateChanged state={window.WindowState}");
        };

        window.LocationChanged += (_, _) =>
        {
            var left = window.Left.ToString("F1", CultureInfo.InvariantCulture);
            var top = window.Top.ToString("F1", CultureInfo.InvariantCulture);
            SmartConLogger.Freeze($"[WhiteDialog:{context}] LocationChanged left={left} top={top}");
        };

        window.Closed += (_, _) =>
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Closed");
            DetachHwndSourceHook(window, context);
        };

        SubscribeTierChangedOnce();
    }

    private static void SubscribeTierChangedOnce()
    {
        if (_tierChangedSubscribed) return;
        _tierChangedSubscribed = true;

        RenderCapability.TierChanged += OnTierChanged;
    }

    private static void OnTierChanged(object? sender, EventArgs e)
    {
        var tier = (RenderCapability.Tier >> 16).ToString(CultureInfo.InvariantCulture);
        SmartConLogger.Freeze($"[WhiteDialog] RenderCapability.TierChanged tier={tier}");
    }

    private static void AttachHwndSourceHook(Window window, string context)
    {
        try
        {
            if (ActiveHooks.TryGetValue(window, out _))
                return;

            var hwndSource = PresentationSource.FromVisual(window) as HwndSource;
            if (hwndSource is null)
            {
                SmartConLogger.Freeze($"[WhiteDialog:{context}] AttachHwndSourceHook skipped: HwndSource not available");
                return;
            }

            HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (!IsNoisyMessage(msg))
                {
                    var msgName = MessageToString(msg);
                    SmartConLogger.Freeze(
                        $"[WhiteDialog:{context}] WndProc " +
                        $"hwnd={hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture)} " +
                        $"msg={msgName}({msg.ToString(CultureInfo.InvariantCulture)}) " +
                        $"wParam={wParam.ToInt64().ToString("X", CultureInfo.InvariantCulture)} " +
                        $"lParam={lParam.ToInt64().ToString("X", CultureInfo.InvariantCulture)}");
                }

                if (msg == WM_PAINT)
                {
                    LogSnapshot(window, context, "OnWmPaint");
                }
                else if (msg == WM_ERASEBKGND)
                {
                    LogSnapshot(window, context, "OnWmEraseBkgnd");
                }

                return IntPtr.Zero;
            };

            hwndSource.AddHook(hook);
            ActiveHooks.Add(window, hook);
            SmartConLogger.Freeze($"[WhiteDialog:{context}] HwndSource.AddHook OK hwnd={hwndSource.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] HwndSource.AddHook FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DetachHwndSourceHook(Window window, string context)
    {
        try
        {
            if (!ActiveHooks.TryGetValue(window, out var hook))
                return;

            var hwndSource = PresentationSource.FromVisual(window) as HwndSource;
            hwndSource?.RemoveHook(hook);
            ActiveHooks.Remove(window);
            SmartConLogger.Freeze($"[WhiteDialog:{context}] HwndSource.RemoveHook OK");
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] HwndSource.RemoveHook FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogSnapshot(Window window, string context, string tag)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var fgHwnd = GetForegroundWindow();
            _ = GetWindowThreadProcessId(fgHwnd, out var fgPid);
            var dispatcher = window.Dispatcher;
            var renderTier = (RenderCapability.Tier >> 16).ToString(CultureInfo.InvariantCulture);
            var posted = Interlocked.Read(ref _postedOperations);
            var completed = Interlocked.Read(ref _completedOperations);
            var pending = (posted - completed).ToString(CultureInfo.InvariantCulture);
            var workingSetMb = (Environment.WorkingSet / 1024 / 1024).ToString(CultureInfo.InvariantCulture);
            var appWindows = Application.Current?.Windows.Count.ToString(CultureInfo.InvariantCulture) ?? "null";

            SmartConLogger.Freeze(
                $"[WhiteDialog:{context}] Snapshot[{tag}] " +
                $"hwnd={hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture)} " +
                $"fgHwnd={fgHwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture)}(pid={fgPid.ToString(CultureInfo.InvariantCulture)}) " +
                $"visible={IsWindowVisible(hwnd).ToString(CultureInfo.InvariantCulture)} " +
                $"enabled={IsWindowEnabled(hwnd).ToString(CultureInfo.InvariantCulture)} " +
                $"active={window.IsActive} " +
                $"actual={window.ActualWidth.ToString("F1", CultureInfo.InvariantCulture)}x" +
                $"{window.ActualHeight.ToString("F1", CultureInfo.InvariantCulture)} " +
                $"state={window.WindowState} " +
                $"dispatcherThread={dispatcher.Thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture)} " +
                $"dispatcherShutdownStarted={dispatcher.HasShutdownStarted} " +
                $"dispatcherShutdownFinished={dispatcher.HasShutdownFinished} " +
                $"renderTier={renderTier} " +
                $"dispatcherPending={pending} " +
                $"workingSetMB={workingSetMb} " +
                $"appWindows={appWindows}");

            SmartConLogger.FreezeThreadPool($"WhiteDialog:{context}:{tag}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[WhiteDialog:{context}] Snapshot[{tag}] FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsNoisyMessage(int msg)
    {
        return msg == WM_MOUSEMOVE ||
               msg == WM_NCHITTEST ||
               msg == WM_SETCURSOR ||
               msg == WM_TIMER ||
               msg == WM_NULL;
    }

    private static string MessageToString(int msg)
    {
        return msg switch
        {
            WM_NULL => "WM_NULL",
            WM_CREATE => "WM_CREATE",
            WM_DESTROY => "WM_DESTROY",
            WM_MOVE => "WM_MOVE",
            WM_SIZE => "WM_SIZE",
            WM_ACTIVATE => "WM_ACTIVATE",
            WM_SETFOCUS => "WM_SETFOCUS",
            WM_KILLFOCUS => "WM_KILLFOCUS",
            WM_PAINT => "WM_PAINT",
            WM_ERASEBKGND => "WM_ERASEBKGND",
            WM_SHOWWINDOW => "WM_SHOWWINDOW",
            WM_ACTIVATEAPP => "WM_ACTIVATEAPP",
            WM_GETMINMAXINFO => "WM_GETMINMAXINFO",
            WM_SETCURSOR => "WM_SETCURSOR",
            WM_NCHITTEST => "WM_NCHITTEST",
            WM_NCPAINT => "WM_NCPAINT",
            WM_NCACTIVATE => "WM_NCACTIVATE",
            WM_SYSCOMMAND => "WM_SYSCOMMAND",
            WM_TIMER => "WM_TIMER",
            WM_MOUSEMOVE => "WM_MOUSEMOVE",
            WM_DWMCOMPOSITIONCHANGED => "WM_DWMCOMPOSITIONCHANGED",
            WM_DWMNCRENDERINGCHANGED => "WM_DWMNCRENDERINGCHANGED",
            _ => "0x" + msg.ToString("X4", CultureInfo.InvariantCulture),
        };
    }
#endif
}
