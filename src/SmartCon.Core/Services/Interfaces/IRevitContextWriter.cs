namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Обновление контекста Revit.
/// Вызывается из IExternalCommand.Execute() и IExternalEventHandler.Execute()
/// перед любой работой с IRevitContext.
/// Отделён от IRevitContext по ISP (Interface Segregation Principle).
/// Параметр object — чтобы Core не зависел от RevitAPIUI (I-09). Передавать UIApplication.
/// </summary>
public interface IRevitContextWriter
{
    void SetContext(object revitUIApplication);
}
