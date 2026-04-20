namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Revit context update.
/// Called from IExternalCommand.Execute() and IExternalEventHandler.Execute()
/// before any work with IRevitContext.
/// Separated from IRevitContext per ISP (Interface Segregation Principle).
/// Parameter is object so Core does not depend on RevitAPIUI (I-09). Pass UIApplication.
/// </summary>
public interface IRevitContextWriter
{
    /// <summary>
    /// Update the context with the current Revit application state.
    /// Pass <c>UIApplication</c> as <see cref="object"/> to avoid RevitAPIUI dependency in Core (I-09).
    /// </summary>
    void SetContext(object revitUIApplication);
}
