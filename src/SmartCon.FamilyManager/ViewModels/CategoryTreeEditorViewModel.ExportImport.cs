using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel
{
    [RelayCommand]
    private async Task ExportCategoriesAsync()
    {
        await ExportAsync(
            () => _packageService.ExportCategoriesAsync(),
            StringLocalization.Keys.FM_CTE_ExportTree,
            "categories");
    }

    [RelayCommand]
    private async Task ExportAttributesAsync()
    {
        await ExportAsync(
            () => _packageService.ExportAttributesAsync(),
            StringLocalization.Keys.FM_CTE_ExportAttrs,
            "attributes");
    }

    [RelayCommand]
    private async Task ExportFullAsync()
    {
        await ExportAsync(
            () => _packageService.ExportFullAsync(),
            StringLocalization.Keys.FM_CTE_ExportFull,
            "smartcon-metadata");
    }

    private async Task ExportAsync<T>(Func<Task<T>> exporter, string titleKey, string defaultFileName)
    {
        var title = LanguageManager.GetString(titleKey) ?? "Export";
        var path = _dialogService.ShowSaveJsonDialog(title, defaultFileName);
        if (path is null) return;

        try
        {
            var package = await exporter();
            var json = JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true });
            await Task.Run(() => File.WriteAllText(path, json));
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Exported) ?? "Exported to {0}", path);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportFromJson()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ImportTree) ?? "Import Metadata";
        var path = _dialogService.ShowOpenJsonDialog(title);
        if (path is null) return;

        try
        {
            var json = File.ReadAllText(path);
            var package = JsonSerializer.Deserialize<FamilyMetadataPackage>(json);
            if (package is null) return;

            var importedNodes = new List<CategoryNodeViewModel>();
            foreach (var cat in package.Categories)
            {
                importedNodes.AddRange(ImportCategoryNode(cat, null, 0));
            }

            RootNodes = new ObservableCollection<CategoryNodeViewModel>(importedNodes);
            _bindingChanges.Clear();
            _pendingImportPackage = package;
            SelectedNode = null;
            UpdateHasUnsavedChanges();
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Imported) ?? "Imported {0} categories",
                importedNodes.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}", ex.Message);
        }
    }

    private static List<CategoryNodeViewModel> ImportCategoryNode(FamilyMetadataCategoryNode node, string? parentId, int sortOrder)
    {
        var result = new List<CategoryNodeViewModel>();
        var categoryId = Guid.NewGuid().ToString();
        var vm = new CategoryNodeViewModel(categoryId, node.Name, parentId, node.Name)
        {
            SortOrder = sortOrder,
            OriginalSortOrder = sortOrder,
            IsNew = true,
            IsDirty = true
        };
        result.Add(vm);

        for (int i = 0; i < node.Children.Count; i++)
        {
            var children = ImportCategoryNode(node.Children[i], categoryId, i);
            foreach (var child in children)
            {
                vm.Children.Add(child);
            }
        }

        return result;
    }
}
