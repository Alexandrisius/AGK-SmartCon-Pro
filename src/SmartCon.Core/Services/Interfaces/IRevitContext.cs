using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Доступ к актуальному Document. Не кешировать — запрашивать при каждой операции (I-05).
/// UIDocument не экспонируется в Core (I-09: запрет Autodesk.Revit.UI).
/// Для selection используй IElementSelectionService.
/// </summary>
public interface IRevitContext
{
    Document GetDocument();
    string GetRevitVersion();
}
