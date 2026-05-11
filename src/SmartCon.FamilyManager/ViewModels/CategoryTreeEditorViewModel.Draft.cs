using System.Collections.ObjectModel;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryTreeEditorViewModel
{
    private async Task<List<EffectiveCategoryAttribute>> GetDraftEffectiveAttributesAsync(
        CategoryNodeViewModel node, Dictionary<string, string> categoryNameById)
    {
        var result = new Dictionary<string, EffectiveCategoryAttribute>();

        // Step 1: Load base effective attrs
        if (!node.IsNew)
        {
            var dbEffective = await _bindingService.GetEffectiveAttributesAsync(node.CategoryId);
            foreach (var attr in dbEffective)
            {
                result[attr.AttributeId] = attr;
            }

            // Apply ALL ancestor draft changes recursively
            await ApplyAncestorDraftChangesAsync(node.ParentId, result);
        }
        else if (node.ParentId is not null)
        {
            // Draft node: inherit from parent recursively with draft awareness
            var parent = FindNodeById(RootNodes, node.ParentId);
            if (parent is not null)
            {
                var parentEffective = await GetDraftEffectiveAttributesAsync(parent, categoryNameById);
                foreach (var attr in parentEffective)
                {
                    result[attr.AttributeId] = attr with { IsInherited = true };
                }
            }
            else
            {
                var dbEffective = await _bindingService.GetEffectiveAttributesAsync(node.ParentId);
                foreach (var attr in dbEffective)
                {
                    result[attr.AttributeId] = attr with { IsInherited = true };
                }
            }
        }

        // Step 2: Apply own draft changes (add/remove bindings)
        var ownChanges = _bindingChanges
            .Where(c =>
            {
                var parts = c.Key.Split(new[] { ':' }, 2);
                return parts.Length == 2 && parts[0] == node.CategoryId;
            })
            .ToList();

        foreach (var change in ownChanges)
        {
            var parts = change.Key.Split(new[] { ':' }, 2);
            var attrId = parts[1];

            if (change.Value)
            {
                // Added binding
                if (!result.ContainsKey(attrId))
                {
                    var attrDef = await _attributeDefRepository.GetByIdAsync(attrId);
                    if (attrDef is not null)
                    {
                        result[attrId] = new EffectiveCategoryAttribute(
                            attrId,
                            attrDef.Name,
                            attrDef.Group,
                            result.Count,
                            true,
                            false, // direct binding, not inherited
                            node.CategoryId);
                    }
                }
            }
            else
            {
                // Removed binding
                result.Remove(attrId);
            }
        }

        return [..result.Values];
    }

    private List<CategoryAttributeBinding> GetDraftDirectBindings(CategoryNodeViewModel node)
    {
        var result = new List<CategoryAttributeBinding>();

        foreach (var change in _bindingChanges)
        {
            var parts = change.Key.Split(new[] { ':' }, 2);
            if (parts.Length != 2 || parts[0] != node.CategoryId) continue;

            if (change.Value)
            {
                result.Add(new CategoryAttributeBinding(
                    Guid.NewGuid().ToString(),
                    node.CategoryId,
                    parts[1],
                    result.Count,
                    true));
            }
        }

        return result;
    }

    private async Task ApplyAncestorDraftChangesAsync(
        string? parentId,
        Dictionary<string, EffectiveCategoryAttribute> result)
    {
        if (parentId is null) return;

        // Check if this ancestor has draft changes
        var parentChanges = _bindingChanges
            .Where(c => c.Key.StartsWith($"{parentId}:"))
            .ToList();

        foreach (var change in parentChanges)
        {
            var parts = change.Key.Split(new[] { ':' }, 2);
            if (parts.Length != 2) continue;
            var attrId = parts[1];
            var isBound = change.Value;

            if (isBound && !result.ContainsKey(attrId))
            {
                var attrDef = await _attributeDefRepository.GetByIdAsync(attrId);
                if (attrDef is not null)
                {
                    result[attrId] = new EffectiveCategoryAttribute(
                        attrId,
                        attrDef.Name,
                        attrDef.Group,
                        result.Count,
                        true,
                        true,
                        parentId);
                }
            }
            else if (!isBound && result.ContainsKey(attrId) && result[attrId].SourceCategoryId == parentId)
            {
                result.Remove(attrId);
            }
        }

        // Recursively apply grandparent's draft changes
        var parent = FindNodeById(RootNodes, parentId);
        if (parent is not null)
        {
            await ApplyAncestorDraftChangesAsync(parent.ParentId, result);
        }
        else
        {
            // Parent only in DB - get its parentId from DB
            var parentNode = await _categoryRepository.GetByIdAsync(parentId);
            await ApplyAncestorDraftChangesAsync(parentNode?.ParentId, result);
        }
    }

    private static CategoryNodeViewModel? FindNodeById(ObservableCollection<CategoryNodeViewModel> nodes, string categoryId)
    {
        foreach (var node in nodes)
        {
            if (node.CategoryId == categoryId) return node;
            var found = FindNodeById(node.Children, categoryId);
            if (found is not null) return found;
        }
        return null;
    }

    private static CategoryNodeViewModel? FindNodeById(ObservableCollection<CatalogTreeNodeViewModel> nodes, string categoryId)
    {
        foreach (var node in nodes)
        {
            if (node is CategoryNodeViewModel cat && cat.CategoryId == categoryId) return cat;
            var found = FindNodeById(node.Children, categoryId);
            if (found is not null) return found;
        }
        return null;
    }
}
