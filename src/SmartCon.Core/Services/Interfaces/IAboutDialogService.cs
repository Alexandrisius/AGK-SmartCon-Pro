namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Opens the application's About dialog (version info, update channel,
/// changelog, update check). Declared in Core so feature modules
/// (e.g. FamilyManager's plugin-compatibility banner, ADR-058) can route
/// the user to the existing update flow without referencing the App layer.
/// Implemented in SmartCon.App.
/// </summary>
public interface IAboutDialogService
{
    void ShowAbout();
}
