using System.Runtime.InteropServices;
using System.Windows.Threading;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.App.Services;

/// <summary>
/// Реализация IWindowFocusService для восстановления фокуса после нативных диалогов Revit.
/// Работает с Win32 API (SetForegroundWindow) и WPF Dispatcher для принудительного обновления UI.
/// </summary>
public sealed class RevitWindowFocusService : IWindowFocusService
{
    private readonly IRevitContext _revitContext;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool BringWindowToTop(IntPtr hWnd);

    public RevitWindowFocusService(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    public void RestoreFocusAndRefreshUI()
    {
        try
        {
            if (_revitContext is not RevitContext revitCtx)
                return;

            var uiApp = revitCtx.GetUIApplication();
            var revitHandle = uiApp.MainWindowHandle;

            if (revitHandle == IntPtr.Zero)
                return;

            var currentForeground = GetForegroundWindow();

            // Активируем Revit, если он не на переднем плане.
            // Это "проталкивает" COM сообщения и помогает разблокировать WPF render thread,
            // который может "застрять" после нативного диалога обновления семейства.
            if (currentForeground != revitHandle)
            {
                SetForegroundWindow(revitHandle);
                BringWindowToTop(revitHandle);
            }

            // Принудительно обновляем WPF render thread через Dispatcher.
            // Пустой Invoke с DispatcherPriority.Render гарантирует, что очередь render
            // будет обработана, что "оживляет" WPF UI после зависания.
            var dispatcher = System.Windows.Application.Current?.Dispatcher
                ?? Dispatcher.CurrentDispatcher;

            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[WindowFocus] Failed to restore focus: {ex.Message}");
        }
    }
}
