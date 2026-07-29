using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Json;
using SmartCon.FamilyManager.Models.Metadata;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel
{
    [RelayCommand]
    private async Task ExportCategoriesAsync()
    {
        await ExportToJsonAsync(
            BuildCategoriesPackageAsync,
            StringLocalization.Keys.FM_CTE_ExportTree,
            "categories");
    }

    [RelayCommand]
    private async Task ExportAttributesAsync()
    {
        await ExportToJsonAsync(
            BuildAttributesPackageAsync,
            StringLocalization.Keys.FM_CTE_ExportAttrs,
            "attributes");
    }

    [RelayCommand]
    private async Task ExportFullAsync()
    {
        await ExportToJsonAsync(
            BuildFullPackageAsync,
            StringLocalization.Keys.FM_CTE_ExportFull,
            "smartcon-metadata");
    }

    private async Task<MetadataExportPackage> BuildCategoriesPackageAsync(CancellationToken ct)
    {
        var categories = await _categoryRepository.GetAllAsync(ct);
        var tree = new CategoryTree(categories);

        return new MetadataExportPackage
        {
            Sections = new MetadataExportSections { Categories = true, Attributes = false, Bindings = false },
            Categories = BuildExportCategoryTree(tree, null)
        };
    }

    private async Task<MetadataExportPackage> BuildAttributesPackageAsync(CancellationToken ct)
    {
        var attributes = await _attributeDefRepository.GetAllAsync(ct);

        return new MetadataExportPackage
        {
            Sections = new MetadataExportSections { Categories = false, Attributes = true, Bindings = false },
            Attributes = attributes.Select(a => new MetadataExportAttribute
            {
                Name = a.Name,
                Group = a.Group
            }).ToList()
        };
    }

    private async Task<MetadataExportPackage> BuildFullPackageAsync(CancellationToken ct)
    {
        var categories = await _categoryRepository.GetAllAsync(ct);
        var tree = new CategoryTree(categories);

        var attributes = await _attributeDefRepository.GetAllAsync(ct);
        var attrById = attributes.ToDictionary(a => a.Id, a => a, StringComparer.OrdinalIgnoreCase);

        var bindings = new List<MetadataExportBinding>();
        foreach (var cat in categories)
        {
            var directBindings = await _bindingService.GetDirectBindingsAsync(cat.Id, ct);
            foreach (var binding in directBindings)
            {
                var catPath = tree.BuildFullPath(binding.CategoryId);
                var attrName = attrById.TryGetValue(binding.AttributeId, out var attr)
                    ? attr.Name
                    : null;

                if (attrName is null) continue;

                // Import Validation Gate (package v3): rules travel inside
                // their binding — the package stays self-contained.
                var rules = await _ruleRepository.GetRulesForBindingAsync(binding.Id, ct);

                bindings.Add(new MetadataExportBinding
                {
                    CategoryPath = catPath,
                    AttributeName = attrName,
                    SortOrder = binding.SortOrder,
                    IsEnabled = binding.IsEnabled,
                    ValidationRules = rules.Select(r => new MetadataExportValidationRule
                    {
                        Operator = r.Operator.ToString(),
                        ValueText = r.ValueText,
                        ValueNumber = r.ValueNumber,
                        MinValue = r.MinValue,
                        MaxValue = r.MaxValue,
                        IsEnabled = r.IsEnabled,
                    }).ToList()
                });
            }
        }

        return new MetadataExportPackage
        {
            Sections = new MetadataExportSections { Categories = true, Attributes = true, Bindings = true },
            Categories = BuildExportCategoryTree(tree, null),
            Attributes = attributes.Select(a => new MetadataExportAttribute
            {
                Name = a.Name,
                Group = a.Group
            }).ToList(),
            Bindings = bindings
        };
    }

    private static List<MetadataExportCategoryNode> BuildExportCategoryTree(CategoryTree tree, string? parentId)
    {
        var result = new List<MetadataExportCategoryNode>();
        foreach (var node in tree.GetChildren(parentId))
        {
            result.Add(new MetadataExportCategoryNode
            {
                Name = node.Name,
                Children = BuildExportCategoryTree(tree, node.Id)
            });
        }
        return result;
    }

    private async Task ExportToJsonAsync(
        Func<CancellationToken, Task<MetadataExportPackage>> packageFactory,
        string titleKey,
        string defaultFileName)
    {
        var title = LanguageManager.GetString(titleKey) ?? "Export";
        var path = _dialogService.ShowSaveJsonDialog(title, defaultFileName);
        if (path is null) return;

        try
        {
            var package = await packageFactory(CancellationToken.None);
            var json = JsonSerializer.Serialize(package, JsonOptions.RelaxedWriteIndented);
            await Task.Run(() => File.WriteAllText(path, json));
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_Exported) ?? "Exported to {0}",
                path);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
    }

    /// <summary>
    /// Atomic metadata import: the whole package (categories, attributes,
    /// bindings, validation rules) is written to the catalog database
    /// immediately — NOT staged into the draft. The editor then reloads
    /// from the DB, so imported bindings and rule badges are visible at
    /// once (no OK + reopen).
    /// </summary>
    [RelayCommand]
    private async Task ImportFromJsonAsync()
    {
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ImportTree) ?? "Import Metadata";
        var path = _dialogService.ShowOpenJsonDialog(title);
        if (path is null) return;

        try
        {
            var json = await Task.Run(() => File.ReadAllText(path));
            var package = JsonSerializer.Deserialize<MetadataExportPackage>(json, JsonOptions.Default);
            if (package is null)
            {
                StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: empty file";
                return;
            }

            package = NormalizeImportedPackage(package);

            var (categoriesCreated, categoriesReused) = await ImportCategoriesToDbAsync(package.Categories);
            var attributesImported = await ImportAttributesAsync(package.Attributes);
            var bindingResult = await ImportBindingsToDbAsync(package.Bindings);

            // Reload the editor from the DB: the tree, the binding
            // checkboxes and the rule badges all reflect the import
            // immediately.
            SelectedNode = null;
            await LoadTreeAsync();
            _metadataMediator.RaiseMetadataChanged();

            var summary = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ImportedFull)
                    ?? "Imported: {0} new categories ({1} existing), {2} attributes, {3} bindings, {4} rules",
                categoriesCreated, categoriesReused, attributesImported,
                bindingResult.BindingsImported, bindingResult.RulesImported);
            var skippedTotal = bindingResult.BindingsSkipped + bindingResult.RulesSkipped;
            if (skippedTotal > 0)
            {
                summary += string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ImportedSkipped)
                        ?? " (skipped: {0})",
                    skippedTotal);
            }

            StatusMessage = summary;

            SmartConLogger.Info(
                $"ImportFromJson: categoriesCreated={categoriesCreated}, categoriesReused={categoriesReused}, " +
                $"attributesImported={attributesImported}, bindings={bindingResult.BindingsImported} " +
                $"(skipped={bindingResult.BindingsSkipped}), rules={bindingResult.RulesImported} " +
                $"(skipped={bindingResult.RulesSkipped}), warnings={bindingResult.Warnings.Count}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"CategoryTreeEditor.ImportFromJson: failed: {ex}");
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Import error: {0}",
                ex.Message);
        }
    }

    /// <summary>
    /// Writes package categories directly to the DB with dedupe by
    /// FullPath: an existing category is reused (its bindings merge in
    /// the next step), a missing one is created level by level so
    /// children get the real parent id.
    /// </summary>
    private async Task<(int Created, int Reused)> ImportCategoriesToDbAsync(List<MetadataExportCategoryNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return (0, 0);
        }

        var existingByPath = (await _categoryRepository.GetAllAsync())
            .ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var reused = 0;

        async Task ImportNodeAsync(MetadataExportCategoryNode node, string? parentId, string parentPath, int sortOrder)
        {
            var path = string.IsNullOrEmpty(parentPath) ? node.Name : $"{parentPath} > {node.Name}";

            string realId;
            if (existingByPath.TryGetValue(path, out var existingCat))
            {
                realId = existingCat.Id;
                reused++;
            }
            else
            {
                var newCat = await _categoryRepository.AddAsync(node.Name, parentId, sortOrder);
                realId = newCat.Id;
                existingByPath[path] = newCat;
                created++;
            }

            for (var i = 0; i < node.Children.Count; i++)
            {
                await ImportNodeAsync(node.Children[i], realId, path, i);
            }
        }

        foreach (var node in nodes)
        {
            await ImportNodeAsync(node, null, string.Empty, 0);
        }

        return (created, reused);
    }

    private async Task<int> ImportAttributesAsync(List<MetadataExportAttribute> attributes)
    {
        if (attributes.Count == 0) return 0;

        var imported = 0;
        foreach (var attr in attributes)
        {
            if (string.IsNullOrWhiteSpace(attr.Name))
            {
                continue;
            }

            var exists = await _attributeDefRepository.NameExistsAsync(attr.Name, null);
            if (exists)
            {
                continue;
            }

            await _attributeDefRepository.CreateAsync(attr.Name, attr.Group);
            imported++;
        }
        return imported;
    }

    private static MetadataExportPackage NormalizeImportedPackage(MetadataExportPackage source)
    {
        if (source.Sections is not null
            && source.Categories is not null
            && source.Attributes is not null
            && source.Bindings is not null)
        {
            return source;
        }

        return new MetadataExportPackage
        {
            Format = source.Format,
            Version = source.Version,
            ExportedAtUtc = source.ExportedAtUtc,
            Sections = source.Sections ?? new MetadataExportSections(),
            Categories = source.Categories ?? [],
            Attributes = source.Attributes ?? [],
            Bindings = source.Bindings ?? []
        };
    }
}
