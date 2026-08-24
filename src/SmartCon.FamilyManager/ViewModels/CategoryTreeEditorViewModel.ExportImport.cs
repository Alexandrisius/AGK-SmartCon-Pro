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
            var assignmentResult = await ImportAssignmentRulesToDbAsync(package.AssignmentRules);

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
            if (assignmentResult.GroupsImported > 0)
            {
                summary += ", " + string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_CTE_ImportedAssignment)
                        ?? "assignment groups: {0}",
                    assignmentResult.GroupsImported);
            }
            var skippedTotal = bindingResult.BindingsSkipped + bindingResult.RulesSkipped
                + assignmentResult.GroupsSkipped + assignmentResult.ConditionsSkipped;
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
                $"(skipped={bindingResult.RulesSkipped}), assignmentGroups={assignmentResult.GroupsImported} " +
                $"(skipped={assignmentResult.GroupsSkipped}, conditionsSkipped={assignmentResult.ConditionsSkipped}), " +
                $"warnings={bindingResult.Warnings.Count + assignmentResult.Warnings.Count}");
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
            && source.Bindings is not null
            && source.AssignmentRules is not null)
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
            Bindings = source.Bindings ?? [],
            AssignmentRules = source.AssignmentRules ?? []
        };
    }

    /// <summary>
    /// Writes package assignment rules to the DB (#241). Policy mirrors
    /// the validation-rule import: existing groups of a category are never
    /// overwritten (package groups are appended); a condition whose
    /// attribute/operator/system key cannot be resolved is skipped with a
    /// warning; a group that lost all its conditions is not created.
    /// </summary>
    private async Task<AssignmentImportResult> ImportAssignmentRulesToDbAsync(
        List<MetadataExportAssignmentRule> assignmentRules)
    {
        var groupsImported = 0;
        var groupsSkipped = 0;
        var conditionsSkipped = 0;
        var warnings = new List<string>();

        if (assignmentRules.Count == 0)
        {
            return new AssignmentImportResult(0, 0, 0, warnings);
        }

        var allCategories = await _categoryRepository.GetAllAsync();
        var pathToCategory = allCategories
            .ToDictionary(c => c.FullPath, c => c, StringComparer.OrdinalIgnoreCase);

        var allAttributes = await _attributeDefRepository.GetAllAsync();
        var nameToAttr = allAttributes
            .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);

        foreach (var rule in assignmentRules)
        {
            if (!pathToCategory.TryGetValue(rule.CategoryPath, out var category))
            {
                warnings.Add($"Assignment rule skipped: category '{rule.CategoryPath}' not found.");
                groupsSkipped += rule.Groups.Count;
                conditionsSkipped += rule.Groups.Sum(g => g.Conditions.Count);
                continue;
            }

            foreach (var group in rule.Groups)
            {
                var importedConditions = 0;
                AssignmentRuleGroup? createdGroup = null;

                foreach (var condition in group.Conditions)
                {
                    var parsed = ParseAssignmentCondition(condition, nameToAttr, warnings);
                    if (parsed is null)
                    {
                        conditionsSkipped++;
                        continue;
                    }

                    createdGroup ??= await _assignmentRuleRepository.CreateGroupAsync(category.Id);
                    await _assignmentRuleRepository.CreateConditionAsync(
                        createdGroup.Id,
                        parsed.Value.SourceKind,
                        parsed.Value.AttributeId,
                        parsed.Value.SystemField,
                        parsed.Value.Operator,
                        condition.ValueText,
                        condition.ValueNumber,
                        condition.MinValue,
                        condition.MaxValue,
                        condition.IsEnabled);
                    importedConditions++;
                }

                if (createdGroup is null)
                {
                    groupsSkipped++;
                    continue;
                }

                if (!group.IsEnabled)
                {
                    await _assignmentRuleRepository.UpdateGroupAsync(createdGroup.Id, group.SortOrder, false);
                }

                groupsImported++;
            }
        }

        return new AssignmentImportResult(groupsImported, groupsSkipped, conditionsSkipped, warnings);
    }

    private static (AssignmentConditionSourceKind SourceKind, string? AttributeId, AssignmentSystemField? SystemField, ValidationRuleOperator Operator)?
        ParseAssignmentCondition(
            MetadataExportAssignmentCondition condition,
            Dictionary<string, AttributeDefinition> nameToAttr,
            List<string> warnings)
    {
        if (!Enum.TryParse<AssignmentConditionSourceKind>(condition.SourceKind, out var sourceKind)
            || !Enum.IsDefined(typeof(AssignmentConditionSourceKind), sourceKind))
        {
            warnings.Add($"Assignment condition skipped: unknown source kind '{condition.SourceKind}'.");
            return null;
        }

        string? attributeId = null;
        AssignmentSystemField? systemField = null;

        if (sourceKind == AssignmentConditionSourceKind.Attribute)
        {
            if (condition.AttributeName is null
                || !nameToAttr.TryGetValue(condition.AttributeName, out var attribute))
            {
                warnings.Add($"Assignment condition skipped: attribute '{condition.AttributeName ?? "<null>"}' not found.");
                return null;
            }

            attributeId = attribute.Id;
        }
        else
        {
            if (!Enum.TryParse<AssignmentSystemField>(condition.SystemKey, out var parsedField)
                || !Enum.IsDefined(typeof(AssignmentSystemField), parsedField))
            {
                warnings.Add($"Assignment condition skipped: unknown system field '{condition.SystemKey ?? "<null>"}'.");
                return null;
            }

            systemField = parsedField;
        }

        if (!Enum.TryParse<ValidationRuleOperator>(condition.Operator, out var ruleOperator)
            || !Enum.IsDefined(typeof(ValidationRuleOperator), ruleOperator)
            || !AssignmentOperatorPolicy.IsAllowed(sourceKind, systemField, ruleOperator))
        {
            warnings.Add($"Assignment condition skipped: operator '{condition.Operator}' is unknown or not allowed.");
            return null;
        }

        return (sourceKind, attributeId, systemField, ruleOperator);
    }

    private sealed record AssignmentImportResult(
        int GroupsImported,
        int GroupsSkipped,
        int ConditionsSkipped,
        IReadOnlyList<string> Warnings);
}
