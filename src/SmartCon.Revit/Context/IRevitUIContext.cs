using Autodesk.Revit.UI;

namespace SmartCon.Revit.Context;

/// <summary>
/// Доступ к UIDocument и UIApplication для Revit-слоя.
/// НЕ экспонируется в Core (I-09: запрет Autodesk.Revit.UI в Core).
/// Реализация: RevitContext.
/// </summary>
public interface IRevitUIContext
{
    UIDocument GetUIDocument();
    UIApplication GetUIApplication();
}
