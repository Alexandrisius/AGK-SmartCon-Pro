using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Globalization;
using SmartCon.Core.Logging;
using SmartCon.FamilyManager.ViewModels;
using System.Linq;
using TreeView = System.Windows.Controls.TreeView;
using TreeViewItem = System.Windows.Controls.TreeViewItem;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollContentPresenter = System.Windows.Controls.ScrollContentPresenter;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace SmartCon.FamilyManager.Behaviors;

public static partial class StickyCategoryHeaderBehavior
{
    /// <summary>
    /// Рекурсивно собирает все <see cref="TreeViewItem"/> с <see cref="CategoryNodeViewModel"/>
    /// из текущего visual tree. Всегда возвращает АКТУАЛЬНЫЙ список — это критично, потому что
    /// вложенные TVI создаются/удаляются при expand/collapse, а root TreeView.ItemContainerGenerator
    /// не уведомляет об изменениях в дочерних генераторах.
    /// </summary>
    private static void CollectCategoryItems(DependencyObject parent, List<TreeViewItem> output)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TreeViewItem tvi && tvi.DataContext is CategoryNodeViewModel)
                output.Add(tvi);

            CollectCategoryItems(child, output);
        }
    }

    private static void RecalculateSticky(StickyState state)
    {
        var overlay = GetOverlay(state.Tree);
        if (overlay is null)
        {
            return;
        }

        if (state.ScrollViewer is null) return;

        var scp = FindVisualChild<ScrollContentPresenter>(state.ScrollViewer);
        if (scp is null) return;

        // Собираем ВСЕ materialised Category TreeViewItem из актуального visual tree.
        // Кэшировать нельзя: вложенные TVI появляются/исчезают при expand/collapse,
        // и root ItemContainerGenerator.StatusChanged об этом НЕ уведомляет.
        var categoryItems = new List<TreeViewItem>(16);
        CollectCategoryItems(state.Tree, categoryItems);

        // Собрать ВСЕ CategoryNodeViewModel из дерева (по VM-иерархии, не по TVI).
        var allCategoryVMs = new List<CategoryNodeViewModel>();
        CollectCategoryVMs(state.Tree.Items, allCategoryVMs);

        var viewportHeight = state.ScrollViewer.ViewportHeight;
        var scrollOffset = state.ScrollViewer.VerticalOffset;

        if (allCategoryVMs.Count == 0)
        {
            ClearOverlay(state);
            if (ShouldLogStickyState(state, signature: "", tviCount: 0, vmCount: 0))
                LogStickyStateOnChange(state, "", allCategoryVMs,
                    [], [], [], 0, scrollOffset, viewportHeight, [], 0);
            return;
        }

        // Маппинг VM → TVI для тех, у которых есть materialised TreeViewItem.
        var vmToTvi = new Dictionary<CategoryNodeViewModel, TreeViewItem>(categoryItems.Count);
        foreach (var tvi in categoryItems)
        {
            if (tvi.DataContext is CategoryNodeViewModel cat)
                vmToTvi[cat] = tvi;
        }

        // Построить tops, heights, parentIndices по индексам allCategoryVMs.
        var topsList = new List<double>(allCategoryVMs.Count);
        var heightsList = new List<double>(allCategoryVMs.Count);
        var parentIndicesList = new List<int>(allCategoryVMs.Count);

        for (int i = 0; i < allCategoryVMs.Count; i++)
        {
            var vm = allCategoryVMs[i];

            if (vmToTvi.TryGetValue(vm, out var tvi))
            {
                var header = FindHeaderRow(tvi);
                if (header is not null)
                {
                    topsList.Add(GetTopInScp(header, scp));
                    heightsList.Add(header.ActualHeight > 0 ? header.ActualHeight : 30);
                }
                else
                {
                    topsList.Add(double.PositiveInfinity);
                    heightsList.Add(FallbackHeaderHeight);
                }
            }
            else
            {
                topsList.Add(double.PositiveInfinity);
                heightsList.Add(FallbackHeaderHeight);
            }

            if (vm.Parent is CategoryNodeViewModel parentVm)
            {
                var parentIdx = allCategoryVMs.IndexOf(parentVm);
                parentIndicesList.Add(parentIdx);
            }
            else
            {
                parentIndicesList.Add(-1);
            }
        }

        // Строим КАСКАД sticky-заголовков: каждый следующий "прилипает" к низу
        // уже показанных заголовков. Это гарантирует что категория закрепляется
        // именно когда она скрывается за уже закреплёнными строками, а не только
        // когда достигает верхней границы viewport.
        var stackIndices = StickyCascadingStackBuilder.BuildCascadingStack(topsList, heightsList, parentIndicesList, viewportHeight);

        var signature = string.Join(",", stackIndices);

        if (stackIndices.Count == 0)
        {
            ClearOverlay(state);
            if (ShouldLogStickyState(state, signature, categoryItems.Count, allCategoryVMs.Count))
                LogStickyStateOnChange(state, signature, allCategoryVMs, topsList, heightsList, parentIndicesList, categoryItems.Count, scrollOffset, viewportHeight, stackIndices, 0);
            return;
        }

        overlay.Children.Clear();

        var overlayStyle = state.Tree.TryFindResource("StickyOverlayCategoryHeaderStyle") as Style;
        var folderGeometry = state.Tree.TryFindResource("FolderIconGeometry") as Geometry;
        var textMuted = state.Tree.TryFindResource("TextMutedBrush") as Brush;
        var backgroundBrush = state.Tree.TryFindResource("BackgroundBrush") as Brush;

        for (int i = 0; i < stackIndices.Count; i++)
        {
            var idx = stackIndices[i];
            var cat = allCategoryVMs[idx];
            vmToTvi.TryGetValue(cat, out var originalTvi);

            // depth = позиция в стеке от корня (i=0 — root, у левого края; i=Count-1 — current, правее).
            var depth = i;
            var item = CreateOverlayItem(cat, originalTvi, depth, state, overlayStyle, folderGeometry, textMuted, backgroundBrush);
            overlay.Children.Add(item);
        }

        if (ShouldLogStickyState(state, signature, categoryItems.Count, allCategoryVMs.Count))
            LogStickyStateOnChange(state, signature, allCategoryVMs, topsList, heightsList, parentIndicesList, categoryItems.Count, scrollOffset, viewportHeight, stackIndices, overlay.Children.Count);
    }

    private static string FormatStackNames(IReadOnlyList<int> indices, IReadOnlyList<CategoryNodeViewModel> vms)
    {
        var sb = new System.Text.StringBuilder(indices.Count * 24);
        for (int i = 0; i < indices.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append('\'').Append(vms[indices[i]].DisplayName).Append('\'');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Дешёвая проверка изменилось ли состояние sticky с прошлого залогированного
    /// вызова. RecalculateSticky вызывается на каждый ScrollChanged/LayoutUpdated,
    /// поэтому логируем только переходы, а не каждый тик скролла (см. #237).
    /// </summary>
    private static bool ShouldLogStickyState(StickyState state, string signature, int tviCount, int vmCount)
        => signature != state.LastLoggedSignature
           || tviCount != state.LastLoggedTviCount
           || vmCount != state.LastLoggedVmCount;

    /// <summary>
    /// Детальное логирование состояния Sticky — вызывается только при изменении
    /// сигнатуры (состав стека) или структуры дерева (число категорий /
    /// materialised TVI — expand/collapse, пересборка TreeNodes).
    /// Полный дамп дерева пишется только при структурном изменении.
    /// </summary>
    private static void LogStickyStateOnChange(
        StickyState state,
        string signature,
        List<CategoryNodeViewModel> allCategoryVMs,
        List<double> topsList,
        List<double> heightsList,
        List<int> parentIndicesList,
        int tviCount,
        double scrollOffset,
        double viewportHeight,
        IReadOnlyList<int> stackIndices,
        int overlayChildren)
    {
        var structureChanged = tviCount != state.LastLoggedTviCount
            || allCategoryVMs.Count != state.LastLoggedVmCount;

        state.LastLoggedSignature = signature;
        state.LastLoggedTviCount = tviCount;
        state.LastLoggedVmCount = allCategoryVMs.Count;

        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(RecalculateSticky)),
            ("ScrollOffset", scrollOffset),
            ("CategoryCount", allCategoryVMs.Count),
            ("MaterializedTviCount", tviCount));

        var occupiedTop = 0.0;
        foreach (var idx in stackIndices)
            occupiedTop += heightsList[idx] > 0 ? heightsList[idx] : 0;

        SmartConLogger.Debug(
            $"Sticky state: scroll={scrollOffset.ToString("F0", CultureInfo.InvariantCulture)} " +
            $"viewport={viewportHeight.ToString("F0", CultureInfo.InvariantCulture)} " +
            $"vmCount={allCategoryVMs.Count} tviCount={tviCount} " +
            $"stack=[{FormatStackNames(stackIndices, allCategoryVMs)}] " +
            $"overlayChildren={overlayChildren} " +
            $"occupiedTop={occupiedTop.ToString("F0", CultureInfo.InvariantCulture)}");

        if (!structureChanged) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("VM tree (idx | depth | name | top | height | parentIdx):");
        for (int i = 0; i < allCategoryVMs.Count; i++)
        {
            var vm = allCategoryVMs[i];
            var depth = ComputeDepth(allCategoryVMs, parentIndicesList, i);
            var topStr = double.IsPositiveInfinity(topsList[i]) ? "+Inf" : topsList[i].ToString("F0", CultureInfo.InvariantCulture);
            sb.AppendLine($"  [{i}] d={depth} '{vm.DisplayName}' top={topStr} h={heightsList[i].ToString("F0", CultureInfo.InvariantCulture)} parent={parentIndicesList[i]}");
        }
        if (stackIndices.Count > 0)
        {
            sb.AppendLine("Stack (root → current):");
            for (int i = 0; i < stackIndices.Count; i++)
            {
                sb.AppendLine($"  stack[{i}] = '{allCategoryVMs[stackIndices[i]].DisplayName}'");
            }
        }

        SmartConLogger.Debug(sb.ToString());
    }

    private static int ComputeDepth(List<CategoryNodeViewModel> allCategoryVMs, List<int> parentIndicesList, int idx)
    {
        var depth = 0;
        var current = idx;
        while (current >= 0 && depth < MaxCategoryDepth)
        {
            current = parentIndicesList[current];
            depth++;
        }
        return depth;
    }

    /// <summary>
    /// Рекурсивно собирает все CategoryNodeViewModel из Items коллекции
    /// (TreeView.Items или vm.Children). FamilyLeafNodeViewModel пропускаются.
    /// </summary>
    private static void CollectCategoryVMs(System.Collections.IEnumerable items, List<CategoryNodeViewModel> output)
    {
        foreach (var item in items)
        {
            if (item is CategoryNodeViewModel cat)
            {
                output.Add(cat);
                CollectCategoryVMs(cat.Children, output);
            }
        }
    }

    private static void ScrollCategoryBelowStickyBar(TreeViewItem originalTvi, StickyState state)
    {
        if (state.ScrollViewer is null) return;

        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(ScrollCategoryBelowStickyBar)));

        var scp = FindVisualChild<ScrollContentPresenter>(state.ScrollViewer);
        if (scp is null)
        {
            SmartConLogger.Debug("No SCP found, falling back to BringIntoView");
            originalTvi.BringIntoView();
            return;
        }

        var header = FindHeaderRow(originalTvi);
        if (header is null)
        {
            SmartConLogger.Debug("No header row found, falling back to BringIntoView");
            originalTvi.BringIntoView();
            return;
        }

        var topInScp = GetTopInScp(header, scp);
        var occupiedTop = state.OverlayBorder?.ActualHeight ?? 0;

        // Сдвигаем скролл так, чтобы header оказался прямо под sticky-bar.
        var targetOffset = state.ScrollViewer.VerticalOffset + topInScp - occupiedTop;
        targetOffset = Math.Max(0, targetOffset);
        targetOffset = Math.Min(targetOffset, state.ScrollViewer.ScrollableHeight);

        SmartConLogger.Debug($"Scrolling: topInScp={topInScp.ToString("F0", CultureInfo.InvariantCulture)} " +
            $"occupiedTop={occupiedTop.ToString("F0", CultureInfo.InvariantCulture)} " +
            $"targetOffset={targetOffset.ToString("F0", CultureInfo.InvariantCulture)} " +
            $"scrollableHeight={state.ScrollViewer.ScrollableHeight.ToString("F0", CultureInfo.InvariantCulture)}");
        state.ScrollViewer.ScrollToVerticalOffset(targetOffset);
    }
}
