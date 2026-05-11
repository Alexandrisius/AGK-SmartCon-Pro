using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void LoadToProject()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.Raise(() =>
        {
            var doc = _revitContext.GetDocument();
            if (doc is null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoActiveDocument) ?? "No active document";
                return;
            }

            try
            {
                var resolved = Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available for this Revit version";
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult();

                if (result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" loaded",
                        result.FamilyName ?? selectedName);

                    var familyName = result.FamilyName ?? selectedName;
                    var loadedFamily = FindLoadedFamily(doc, familyName);
                    if (loadedFamily is not null)
                    {
                        var typeNames = ReadFamilyTypeNames(doc, loadedFamily);
                        if (typeNames.Count > 0)
                            FireAndForget(() => SaveTypesAndReloadTreeAsync(selectedId, typeNames));
                    }

                    var usage = new ProjectFamilyUsage(
                        Id: Guid.NewGuid().ToString(),
                        CatalogItemId: selectedId,
                        VersionId: resolved.VersionId,
                        ProjectName: Path.GetFileName(doc.PathName),
                        ProjectPath: doc.PathName,
                        RevitMajorVersion: targetRevit,
                        Action: "Load",
                        CreatedAtUtc: DateTimeOffset.UtcNow);

                    FireAndForget(() => _usageRepo.RecordUsageAsync(usage, CancellationToken.None));
                }
                else
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void LoadAndPlace()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.RaiseWithApplication(appObj =>
        {
            var uiApp = (UIApplication)appObj;
            var doc = _revitContext.GetDocument();
            if (doc is null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoActiveDocument) ?? "No active document";
                return;
            }

            try
            {
                SmartConLogger.FreezeThreadPool("LoadAndPlace.Start");

                var resolved = SmartConLogger.FreezeTimer("LoadAndPlace.ResolveFile", () =>
                    Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult());

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available";
                    SmartConLogger.Freeze("LoadAndPlace: No version available");
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = SmartConLogger.FreezeTimer("LoadAndPlace.LoadFamily", () =>
                    _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult());

                if (!result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        result.ErrorMessage);
                    SmartConLogger.Freeze($"LoadAndPlace: Load failed - {result.ErrorMessage}");
                    return;
                }

                SmartConLogger.Freeze($"LoadAndPlace: Family '{result.FamilyName}' loaded successfully");

                var familyName = result.FamilyName ?? selectedName;

                var family = FindLoadedFamily(doc, familyName);

                if (family is null)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        LanguageManager.GetString(StringLocalization.Keys.FM_FamilyNotFoundAfterLoad) ?? "Family not found after loading");
                    return;
                }

                var symbolId = family.GetFamilySymbolIds().Cast<ElementId>().FirstOrDefault();
                if (symbolId is null) return;

                var symbol = doc.GetElement(symbolId) as FamilySymbol;
                if (symbol is null) return;

                if (!symbol.IsActive)
                {
                    _transactionService.RunInTransaction("Activate Family Symbol", _ =>
                    {
                        if (!symbol.IsActive)
                            symbol.Activate();
                    });
                }

                var uidoc = uiApp.ActiveUIDocument;
                if (uidoc is not null)
                {
                    uidoc.PostRequestForElementTypePlacement(symbol);
                }

                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadAndPlaceSuccess) ?? "Family \"{0}\" — click to place",
                    familyName);

                var usage = new ProjectFamilyUsage(
                    Id: Guid.NewGuid().ToString(),
                    CatalogItemId: selectedId,
                    VersionId: resolved.VersionId,
                    ProjectName: Path.GetFileName(doc.PathName),
                    ProjectPath: doc.PathName,
                    RevitMajorVersion: targetRevit,
                    Action: "LoadAndPlace",
                    CreatedAtUtc: DateTimeOffset.UtcNow);

                FireAndForget(() => _usageRepo.RecordUsageAsync(usage, CancellationToken.None));
                SmartConLogger.Freeze("LoadAndPlace: Completed successfully");
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
                SmartConLogger.Freeze($"LoadAndPlace: Exception - {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private void PlaceType()
    {
        if (SelectedTreeNode is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        var catalogItemId = leaf.CatalogItemId;
        var familyName = leaf.DisplayName;
        var typeName = typeNode.TypeName;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.RaiseWithApplication(appObj =>
        {
            var uiApp = (UIApplication)appObj;
            var doc = _revitContext.GetDocument();
            if (doc is null) return;

            try
            {
                SmartConLogger.FreezeThreadPool("PlaceType.Start");

                var family = FindLoadedFamily(doc, familyName);

                if (family is null)
                {
                    var resolved = SmartConLogger.FreezeTimer("PlaceType.ResolveFile", () =>
                        _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevit, CancellationToken.None).GetAwaiter().GetResult());

                    if (string.IsNullOrEmpty(resolved.AbsolutePath))
                    {
                        SmartConLogger.Freeze("PlaceType: No file resolved");
                        return;
                    }

                    var loadOptions = FamilyLoadOptions.Default with { PreferredName = familyName };
                    SmartConLogger.FreezeTimer("PlaceType.LoadFamily", () =>
                        _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult());

                    family = FindLoadedFamily(doc, familyName);
                }
                else
                {
                    SmartConLogger.Freeze("PlaceType: Family already loaded");
                }

                if (family is null) return;

                var symbol = FindFamilySymbolByName(doc, family, typeName);
                if (symbol is null) return;

                if (!symbol.IsActive)
                {
                    _transactionService.RunInTransaction("Activate Symbol", _ =>
                    {
                        if (!symbol.IsActive) symbol.Activate();
                    });
                }

                uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(symbol);

                FireAndForget(async () =>
                {
                    await SaveTypesAndReloadTreeAsync(catalogItemId, ReadFamilyTypeNames(doc, family));
                });
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"PlaceType failed: {ex.Message}");
            }
        });
    }

    private static Autodesk.Revit.DB.Family? FindLoadedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ReadFamilyTypeNames(Document doc, Autodesk.Revit.DB.Family family)
    {
        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .Select(s => s.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static FamilySymbol? FindFamilySymbolByName(Document doc, Autodesk.Revit.DB.Family family, string typeName)
    {
        return family.GetFamilySymbolIds()
            .Select(id => doc.GetElement(id))
            .OfType<FamilySymbol>()
            .FirstOrDefault(s => s.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SimpleFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Autodesk.Revit.DB.Family sharedFamily, bool familyInUse,
            out Autodesk.Revit.DB.FamilySource source, out bool overwriteParameterValues)
        {
            source = Autodesk.Revit.DB.FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
