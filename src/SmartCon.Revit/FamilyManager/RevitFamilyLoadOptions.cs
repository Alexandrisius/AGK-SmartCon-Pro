using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Implementation of IFamilyLoadOptions that delegates the shared-nested-family
/// decision to an optional UI callback. When no callback is supplied, behaves
/// with the legacy defaults (FamilySource.Family + overwriteParameterValues = true).
/// </summary>
public sealed class RevitFamilyLoadOptions : IFamilyLoadOptions
{
    private readonly bool _overwriteParameterValues;
    private readonly Action<string>? _onStatusMessage;
    private readonly Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? _onSharedDecision;

    public RevitFamilyLoadOptions(
        bool overwriteParameterValues = true,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null)
    {
        _overwriteParameterValues = overwriteParameterValues;
        _onStatusMessage = onStatusMessage;
        _onSharedDecision = onSharedDecision;
    }

    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyLoadOptions", ("Method", "OnFamilyFound"), ("FamilyInUse", familyInUse));
        SmartConLogger.Info($"called: overwrite={_overwriteParameterValues}");
        overwriteParameterValues = _overwriteParameterValues;
        return true;
    }

    public bool OnSharedFamilyFound(Autodesk.Revit.DB.Family sharedFamily, bool familyInUse, out Autodesk.Revit.DB.FamilySource source, out bool overwriteParameterValues)
    {
        var familyName = sharedFamily?.Name ?? "<null>";
        using var _scope = SmartConLogger.BeginScope("FamilyLoadOptions",
            ("Method", "OnSharedFamilyFound"),
            ("SharedFamily", familyName),
            ("FamilyInUse", familyInUse));

        SmartConLogger.Info($"callback available: {_onSharedDecision is not null}");

        if (_onSharedDecision is not null && sharedFamily is not null)
        {
            var parentName = sharedFamily.Document?.OwnerFamily?.Name ?? string.Empty;
            var request = new SharedFamilyDecisionRequest(
                SharedFamilyName: sharedFamily.Name,
                IsFamilyInUse: familyInUse,
                ParentFamilyName: parentName);

            SharedFamiliesLoadChoice choice;
            try
            {
                choice = _onSharedDecision(request);
                SmartConLogger.Info($"user choice: {choice}");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"callback threw: {ex.GetType().Name}: {ex.Message} [Action: Falling back to Skip — Revit will not load this shared nested]");
                choice = SharedFamiliesLoadChoice.Skip;
            }

            switch (choice)
            {
                case SharedFamiliesLoadChoice.UseProject:
                    SmartConLogger.Info("applying: Use Project (preserve project version)");
                    source = Autodesk.Revit.DB.FamilySource.Project;
                    overwriteParameterValues = false;
                    _onStatusMessage?.Invoke($"Использовано из проекта: {familyName}");
                    return true;

                case SharedFamiliesLoadChoice.OverwriteParameters:
                    SmartConLogger.Info("applying: Overwrite Parameters");
                    source = Autodesk.Revit.DB.FamilySource.Family;
                    overwriteParameterValues = true;
                    _onStatusMessage?.Invoke($"Обновлено (с параметрами): {familyName}");
                    return true;

                case SharedFamiliesLoadChoice.OverwriteAll:
                    SmartConLogger.Info("applying: Overwrite All");
                    source = Autodesk.Revit.DB.FamilySource.Family;
                    overwriteParameterValues = true;
                    _onStatusMessage?.Invoke($"Полная перезапись: {familyName}");
                    return true;

                case SharedFamiliesLoadChoice.Skip:
                    SmartConLogger.Info("applying: Skip → Use Project (user closed dialog without choosing — preserve project version to avoid aborting parent family load)");
                    source = Autodesk.Revit.DB.FamilySource.Project;
                    overwriteParameterValues = false;
                    _onStatusMessage?.Invoke($"Пропущено (использована версия из проекта): {familyName}");
                    return true;
            }
        }

        SmartConLogger.Info($"default branch: overwrite={_overwriteParameterValues}");
        source = Autodesk.Revit.DB.FamilySource.Family;
        overwriteParameterValues = _overwriteParameterValues;

        if (sharedFamily is not null)
        {
            _onStatusMessage?.Invoke($"Обновлено вложенное семейство: {sharedFamily.Name}");
        }

        return true;
    }
}
