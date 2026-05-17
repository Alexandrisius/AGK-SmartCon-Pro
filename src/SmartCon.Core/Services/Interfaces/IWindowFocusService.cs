namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Восстанавливает фокус окна и обновляет WPF UI thread после операций,
/// которые могут вызывать нативные диалоги Revit (например, обновление версии семейства).
/// </summary>
public interface IWindowFocusService
{
    /// <summary>
    /// Восстанавливает фокус на главное окно Revit и принудительно обновляет WPF render thread.
    /// Должен вызываться после операций LoadFamily/OpenDocumentFile с семействами старой версии.
    /// </summary>
    void RestoreFocusAndRefreshUI();
}
