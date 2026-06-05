using System.Collections.ObjectModel;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(HasActiveDatabase))]
    private async Task FindInCatalogAsync()
    {
        try
        {
            await _awaitableEvent.RaiseAsync(appObj =>
            {
                try
                {
                    var app = (Autodesk.Revit.UI.UIApplication)appObj;
                    var uidoc = app.ActiveUIDocument;
                    if (uidoc is null)
                    {
                        StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_FindInCatalog_NoSelection)
                            ?? "No element selected in the model";
                        return;
                    }

                    var selectedIds = uidoc.Selection.GetElementIds();
                    if (selectedIds.Count == 0)
                    {
                        StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_FindInCatalog_NoSelection)
                            ?? "No element selected in the model";
                        return;
                    }

                    var doc = uidoc.Document;
                    string? typeName = null;
                    string? familyName = null;

                    foreach (var id in selectedIds)
                    {
                        var element = doc.GetElement(id);
                        if (element is null) continue;

                        if (element is FamilyInstance fi)
                        {
                            typeName = fi.Symbol.Name;
                            familyName = fi.Symbol.FamilyName;
                            break;
                        }

                        if (element is FamilySymbol fs)
                        {
                            typeName = fs.Name;
                            familyName = fs.FamilyName;
                            break;
                        }

                        if (element is Family fam)
                        {
                            familyName = fam.Name;
                            break;
                        }

                        var elemTypeId = element.GetTypeId();
                        if (elemTypeId is not null && elemTypeId != ElementId.InvalidElementId)
                        {
                            var elemType = doc.GetElement(elemTypeId) as ElementType;
                            if (elemType is not null)
                            {
                                typeName = elemType.Name;
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(typeName) && string.IsNullOrEmpty(familyName))
                    {
                        StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_FindInCatalog_NoSelection)
                            ?? "No element selected in the model";
                        return;
                    }

                    CatalogTreeNodeViewModel? found = null;
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        found = FindTypeNodeByName(TreeNodes, typeName!);
                    }

                    if (found is null && !string.IsNullOrEmpty(familyName))
                    {
                        found = FindFamilyNodeByName(TreeNodes, familyName!);
                    }

                    if (found is null)
                    {
                        var msg = LanguageManager.GetString(StringLocalization.Keys.FM_FindInCatalog_NotFound)
                            ?? "Family '{0}' not found in catalog";
                        StatusMessage = string.Format(msg, typeName ?? familyName);
                        return;
                    }

                    ClearAllSelections(TreeNodes);
                    ExpandParents(TreeNodes, found);
                    found.IsSelected = true;
                    SelectedTreeNode = found;

                    var foundMsg = LanguageManager.GetString(StringLocalization.Keys.FM_FindInCatalog_Found)
                        ?? "Family found: {0}";
                    StatusMessage = string.Format(foundMsg, typeName ?? familyName ?? "");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"FindInCatalog failed: {ex.Message}");
                    StatusMessage = ex.Message;
                }
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FindInCatalog outer failed: {ex.Message}");
        }
    }

    private static CatalogTreeNodeViewModel? FindFamilyNodeByName(ObservableCollection<CatalogTreeNodeViewModel> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyLeafNodeViewModel leaf && leaf.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return leaf;

            var found = FindFamilyNodeByName(node.Children, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static FamilyTypeNodeViewModel? FindTypeNodeByName(ObservableCollection<CatalogTreeNodeViewModel> nodes, string typeName)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyTypeNodeViewModel typeNode && typeNode.TypeName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                return typeNode;

            var found = FindTypeNodeByName(node.Children, typeName);
            if (found is not null) return found;
        }
        return null;
    }

    private static void ClearAllSelections(ObservableCollection<CatalogTreeNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsSelected = false;
            ClearAllSelections(node.Children);
        }
    }

    private static void ExpandParents(ObservableCollection<CatalogTreeNodeViewModel> roots, CatalogTreeNodeViewModel target)
    {
        var path = FindPath(roots, target);
        if (path is null) return;
        foreach (var node in path)
        {
            if (node is CategoryNodeViewModel cat)
                cat.IsExpanded = true;
            else if (node is FamilyLeafNodeViewModel)
                node.IsExpanded = true;
        }
    }

    private static List<CatalogTreeNodeViewModel>? FindPath(ObservableCollection<CatalogTreeNodeViewModel> nodes, CatalogTreeNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (node == target) return new List<CatalogTreeNodeViewModel>();
            var childPath = FindPath(node.Children, target);
            if (childPath is not null)
            {
                childPath.Insert(0, node);
                return childPath;
            }
        }
        return null;
    }
}
