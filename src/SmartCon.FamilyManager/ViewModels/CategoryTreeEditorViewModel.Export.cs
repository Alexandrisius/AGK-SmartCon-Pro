using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
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

        // Auto-assignment rules (package v4, #241).
        var assignmentRules = await BuildAssignmentRulesExportAsync(tree, attrById, ct);

        return new MetadataExportPackage
        {
            Sections = new MetadataExportSections { Categories = true, Attributes = true, Bindings = true },
            Categories = BuildExportCategoryTree(tree, null),
            Attributes = attributes.Select(a => new MetadataExportAttribute
            {
                Name = a.Name,
                Group = a.Group
            }).ToList(),
            Bindings = bindings,
            AssignmentRules = assignmentRules
        };
    }

    private async Task<List<MetadataExportAssignmentRule>> BuildAssignmentRulesExportAsync(
        CategoryTree tree,
        Dictionary<string, AttributeDefinition> attrById,
        CancellationToken ct)
    {
        var groups = await _assignmentRuleRepository.GetGroupsWithConditionsAsync(ct);
        if (groups.Count == 0)
        {
            return [];
        }

        var result = new List<MetadataExportAssignmentRule>();
        foreach (var byCategory in groups.GroupBy(g => g.CategoryId))
        {
            if (tree.GetById(byCategory.Key) is null)
            {
                SmartConLogger.Warn(
                    $"Assignment export: category {byCategory.Key} not found — rules skipped " +
                    "[Action: пересоздайте правила автоназначения для этой категории]");
                continue;
            }

            result.Add(new MetadataExportAssignmentRule
            {
                CategoryPath = tree.BuildFullPath(byCategory.Key),
                Groups = byCategory
                    .OrderBy(g => g.SortOrder)
                    .Select(g => new MetadataExportAssignmentGroup
                    {
                        SortOrder = g.SortOrder,
                        IsEnabled = g.IsEnabled,
                        Conditions = BuildConditionExports(g, attrById)
                    })
                    .ToList()
            });
        }

        return result;
    }

    private static List<MetadataExportAssignmentCondition> BuildConditionExports(
        AssignmentRuleGroup group,
        Dictionary<string, AttributeDefinition> attrById)
    {
        var conditions = new List<MetadataExportAssignmentCondition>();
        foreach (var condition in group.Conditions.OrderBy(c => c.SortOrder))
        {
            string? attributeName = null;
            if (condition.SourceKind == AssignmentConditionSourceKind.Attribute)
            {
                attributeName = condition.AttributeId is not null
                    && attrById.TryGetValue(condition.AttributeId, out var attr)
                        ? attr.Name
                        : null;

                if (attributeName is null)
                {
                    SmartConLogger.Warn(
                        $"Assignment export: condition {condition.Id} references a missing attribute — condition skipped " +
                        "[Action: пересоздайте условие в редакторе правил автоназначения]");
                    continue;
                }
            }

            conditions.Add(new MetadataExportAssignmentCondition
            {
                SourceKind = condition.SourceKind.ToString(),
                AttributeName = attributeName,
                SystemKey = condition.SystemField?.ToString(),
                Operator = condition.Operator.ToString(),
                ValueText = condition.ValueText,
                ValueNumber = condition.ValueNumber,
                MinValue = condition.MinValue,
                MaxValue = condition.MaxValue,
                IsEnabled = condition.IsEnabled,
            });
        }

        return conditions;
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
}
