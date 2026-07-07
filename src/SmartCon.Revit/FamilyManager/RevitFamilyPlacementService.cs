using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реализация IFamilyPlacementService через Revit API.
/// Все методы вызываются только из ExternalEvent handler (I-01).
/// </summary>
public sealed class RevitFamilyPlacementService : IFamilyPlacementService
{
    private readonly IRevitContext _revitContext;
    private readonly IRevitUIContext _revitUIContext;
    private readonly ITransactionService _transactionService;
    private readonly IFamilyLoadService _loadService;
    private readonly ISystemFamilyPlacementService _systemFamilyPlacementService;

    public RevitFamilyPlacementService(
        IRevitContext revitContext,
        IRevitUIContext revitUIContext,
        ITransactionService transactionService,
        IFamilyLoadService loadService,
        ISystemFamilyPlacementService systemFamilyPlacementService)
    {
        _revitContext = revitContext;
        _revitUIContext = revitUIContext;
        _transactionService = transactionService;
        _loadService = loadService;
        _systemFamilyPlacementService = systemFamilyPlacementService;
    }

    public bool ActivateAndPlaceType(string familyName, string typeName)
    {
        using var _scope = SmartConLogger.BeginScope("Placement",
            ("Method", "ActivateAndPlaceType"),
            ("FamilyName", familyName),
            ("TypeName", typeName));
        var doc = _revitContext.GetDocument();
        var uiApp = GetUIApplication();
        if (doc is null || uiApp is null) return false;

        var family = FindFamily(doc, familyName);
        if (family is null)
        {
            SmartConLogger.Warn($"ActivateAndPlaceType: Family '{familyName}' not found");
            return false;
        }

        var symbol = FindSymbol(doc, family, typeName);
        if (symbol is null)
        {
            SmartConLogger.Warn($"ActivateAndPlaceType: Type '{typeName}' not found in family '{familyName}'");
            return false;
        }

        if (!symbol.IsActive)
        {
            _transactionService.RunInTransaction("Activate Family Symbol", _ =>
            {
                if (!symbol.IsActive)
                    symbol.Activate();
            });
        }

        uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(symbol);
        SmartConLogger.Info($"ActivateAndPlaceType: Requested placement of {familyName}:{typeName}");
        return true;
    }

    public void LoadAndPlaceFamily(string filePath, string familyName, string? preferredTypeName = null)
    {
        var doc = _revitContext.GetDocument();
        var uiApp = GetUIApplication();
        if (doc is null || uiApp is null) return;

        var resolved = new SmartCon.Core.Models.FamilyManager.FamilyResolvedFile(filePath, null, null, null);
        var options = SmartCon.Core.Models.FamilyManager.FamilyLoadOptions.Default with { PreferredName = familyName };
        var result = _loadService.LoadFamilyAsync(resolved, options).GetAwaiter().GetResult();

        if (!result.Success)
        {
            SmartConLogger.Warn($"LoadAndPlaceFamily: Failed to load {familyName} - {result.ErrorMessage}");
            return;
        }

        var family = FindFamily(doc, familyName);
        if (family is null)
        {
            SmartConLogger.Warn($"LoadAndPlaceFamily: Family '{familyName}' not found after loading");
            return;
        }

        var typeName = preferredTypeName ?? GetFirstTypeName(doc, family);
        if (typeName is null)
        {
            SmartConLogger.Warn($"LoadAndPlaceFamily: No types found in family '{familyName}'");
            return;
        }

        ActivateAndPlaceType(familyName, typeName);
    }

    public void LoadAndPlaceSystemType(string catalogItemId, string typeName)
    {
        var revitVersion = int.Parse(_revitContext.GetRevitVersion());
        _systemFamilyPlacementService.LoadAndPlaceSystemType(catalogItemId, typeName, revitVersion);
    }

    private static Autodesk.Revit.DB.Family? FindFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
    }

    private static FamilySymbol? FindSymbol(Document doc, Autodesk.Revit.DB.Family family, string typeName)
    {
        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetFirstTypeName(Document doc, Autodesk.Revit.DB.Family family)
    {
        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .Select(s => s.Name)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
    }

    private UIApplication? GetUIApplication()
    {
        return _revitUIContext.GetUIApplication();
    }
}
