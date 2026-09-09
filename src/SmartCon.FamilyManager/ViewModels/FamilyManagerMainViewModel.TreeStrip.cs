using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// Удаляет категории без family items при активном поиске, чтобы пользователь
    /// видел только ветки, содержащие совпадения. При <paramref name="expandAll"/>=<c>false</c>
    /// (поиск неактивен) метод не трогает дерево.
    /// Категория считается пустой, если у неё <c>FamilyCount == 0</c> и ни одна дочерняя
    /// категория не содержит результатов. Идём по списку с конца, чтобы удаление было
    /// безопасным для итерации.
    /// </summary>
    internal static void StripEmptyCategories(ObservableCollection<CatalogTreeNodeViewModel> roots, bool expandAll)
    {
        if (!expandAll || roots is null)
        {
            return;
        }

        var beforeCount = CountAll(roots);
        StripEmptyRecursive(roots);
        var afterCount = CountAll(roots);
        // DIAG-DUMP (Issue: net48 tree-expand after search).
        // Confirms whether search actually pruned empty categories and how many
        // nodes were removed. If the user reports "no categories expanded", the
        // answer to "were there even matching categories?" lives here.
        SmartConLogger.Debug(
            $"FMTree.StripEmptyCategories: pruned {beforeCount - afterCount} empty category node(s) " +
            $"({beforeCount} → {afterCount})");
    }

    private static int CountAll(ObservableCollection<CatalogTreeNodeViewModel> nodes)
    {
        if (nodes is null) return 0;
        int count = nodes.Count;
        foreach (var node in nodes)
        {
            count += CountAll(node.Children);
        }
        return count;
    }

    private static void StripEmptyRecursive(ObservableCollection<CatalogTreeNodeViewModel> nodes)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i] is not CategoryNodeViewModel cat)
            {
                continue;
            }

            StripEmptyRecursive(cat.Children);

            if (cat.FamilyCount == 0 && !HasNonEmptyCategoryDescendant(cat))
            {
                nodes.RemoveAt(i);
            }
        }
    }

    private static bool HasNonEmptyCategoryDescendant(CategoryNodeViewModel category)
    {
        foreach (var child in category.Children)
        {
            if (child is CategoryNodeViewModel childCat)
            {
                if (childCat.FamilyCount > 0 || HasNonEmptyCategoryDescendant(childCat))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
