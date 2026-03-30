using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Context;

/// <summary>
/// Реализация IRevitContext + IRevitContextWriter + IRevitUIContext.
/// Хранит ссылку на UIApplication, обновляемую при каждом Execute().
/// Не кешировать Document/UIDocument — запрашивать при каждой операции (I-05).
/// </summary>
public sealed class RevitContext : IRevitContext, IRevitContextWriter, IRevitUIContext
{
    private UIApplication? _uiApplication;

    /// <summary>
    /// IRevitContextWriter.SetContext — принимает object (UIApplication).
    /// Core-интерфейс использует object чтобы не зависеть от RevitAPIUI (I-09).
    /// </summary>
    public void SetContext(object revitUIApplication)
    {
        _uiApplication = revitUIApplication as UIApplication
            ?? throw new ArgumentException(
                $"Expected UIApplication, got {revitUIApplication?.GetType().Name ?? "null"}",
                nameof(revitUIApplication));
    }

    public Document GetDocument()
    {
        EnsureInitialized();
        return _uiApplication!.ActiveUIDocument.Document;
    }

    /// <summary>
    /// Доступ к UIDocument для Revit-слоя (selection, PickObject).
    /// НЕ экспонируется через Core-интерфейс IRevitContext (I-09).
    /// </summary>
    public UIDocument GetUIDocument()
    {
        EnsureInitialized();
        return _uiApplication!.ActiveUIDocument;
    }

    /// <summary>
    /// Доступ к UIApplication для Revit-слоя.
    /// НЕ экспонируется через Core-интерфейс (I-09).
    /// </summary>
    public UIApplication GetUIApplication()
    {
        EnsureInitialized();
        return _uiApplication!;
    }

    public string GetRevitVersion()
    {
        EnsureInitialized();
        return _uiApplication!.Application.VersionNumber;
    }

    private void EnsureInitialized()
    {
        if (_uiApplication is null)
        {
            throw new InvalidOperationException(
                "RevitContext не инициализирован. Вызовите SetContext() из ExternalEventHandler.Execute().");
        }
    }
}
