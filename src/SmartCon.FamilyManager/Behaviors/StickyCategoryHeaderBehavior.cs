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

/// <summary>
/// Attached behavior: приклеивает Category-заголовки к верху TreeView при вертикальном скролле,
/// формируя стек заголовков всего пути от текущей видимой Category до корня (ADR-032).
/// </summary>
/// <remarks>
/// <para><b>Архитектура — Overlay-подход:</b></para>
/// <list type="bullet">
///   <item>В XAML TreeView оборачивается в Grid, поверх добавляется <c>Border</c> с
///         <c>Panel.ZIndex="1000"</c> и <c>StackPanel</c> внутри — это overlay-слой.</item>
///   <item>На каждый <c>ScrollChanged</c> / <c>LayoutUpdated</c> behavior очищает overlay и
///         добавляет в StackPanel новые <c>Border</c> для каждой Category в стеке.</item>
///   <item>Overlay-элементы имеют <c>IsHitTestVisible=False</c>, поэтому клики проходят
///         сквозь них к оригинальным TreeViewItem (которые остаются видимыми частично).</item>
///   <item>Гарантированный Z-порядок через <c>Panel.ZIndex="1000"</c> — overlay ВСЕГДА
///         поверх TreeView, в отличие от TranslateTransform на TreeViewItem (которая
///         НЕ поднимает Z-порядок относительно sibling TreeViewItem).</item>
/// </list>
/// <para><b>Использование в XAML:</b></para>
/// <code>
/// &lt;Grid&gt;
///     &lt;TreeView behaviors:StickyCategoryHeaderBehavior.Enabled="True"
///               behaviors:StickyCategoryHeaderBehavior.Overlay="{Binding ElementName=StickyStack}"
///               ... /&gt;
///     &lt;Border Panel.ZIndex="1000" IsHitTestVisible="False"
///             VerticalAlignment="Top" HorizontalAlignment="Stretch"&gt;
///         &lt;StackPanel x:Name="StickyStack"/&gt;
///     &lt;/Border&gt;
/// &lt;/Grid&gt;
/// </code>
/// </remarks>
public static class StickyCategoryHeaderBehavior
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(StickyCategoryHeaderBehavior),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    /// <summary>
    /// Overlay-панель (обычно <c>StackPanel</c>), в которую behavior добавляет прилипшие
    /// Category-заголовки. Должна находиться в Grid поверх TreeView с <c>Panel.ZIndex="1000"</c>
    /// и <c>IsHitTestVisible="False"</c>.
    /// </summary>
    public static readonly DependencyProperty OverlayProperty =
        DependencyProperty.RegisterAttached(
            "Overlay",
            typeof(Panel),
            typeof(StickyCategoryHeaderBehavior),
            new PropertyMetadata(null, OnOverlayChanged));

    public static Panel? GetOverlay(DependencyObject obj) => obj.GetValue(OverlayProperty) as Panel;

    public static void SetOverlay(DependencyObject obj, Panel? value) => obj.SetValue(OverlayProperty, value);

    private static readonly Dictionary<TreeView, StickyState> _states = [];

    private const double FallbackHeaderHeight = 30;
    private const int MaxCategoryDepth = 100;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView tree) return;

        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(OnEnabledChanged)),
            ("Enabled", e.NewValue));
        SmartConLogger.Debug("TreeView enabled changed");

        if ((bool)e.NewValue)
            Attach(tree);
        else
            Detach(tree);
    }

    private static void OnOverlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeView tree) return;
        if (!_states.TryGetValue(tree, out var state)) return;

        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(OnOverlayChanged)),
            ("HasOldValue", e.OldValue is not null),
            ("HasNewValue", e.NewValue is not null));

        // Если панель сменилась — очищаем старую и пересчитываем.
        if (e.OldValue is Panel oldPanel)
            oldPanel.Children.Clear();

        UpdateOverlayBorder(state);

        if (state.Attached)
            RecalculateSticky(state);
    }

    private static void UpdateOverlayBorder(StickyState state)
    {
        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(UpdateOverlayBorder)));

        var overlay = GetOverlay(state.Tree);
        var newBorder = overlay is not null ? VisualTreeHelper.GetParent(overlay) as Border : null;
        SmartConLogger.Debug($"overlay={overlay is not null}, newBorder={newBorder is not null}");

        if (ReferenceEquals(state.OverlayBorder, newBorder)) return;

        if (state.OverlayBorder is not null)
            state.OverlayBorder.PreviewMouseWheel -= OnOverlayPreviewMouseWheel;

        state.OverlayBorder = newBorder;

        if (state.OverlayBorder is not null)
            state.OverlayBorder.PreviewMouseWheel += OnOverlayPreviewMouseWheel;
    }

    private static void OnOverlayPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var border = sender as Border;
        var state = _states.Values.FirstOrDefault(s => ReferenceEquals(s.OverlayBorder, border));
        if (state?.ScrollViewer is null) return;

        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(OnOverlayPreviewMouseWheel)),
            ("Delta", e.Delta));

        // Перенаправляем wheel в ScrollViewer TreeView, иначе скролл не работает
        // когда курсор над sticky-областью (overlay перехватывает hit-test).
        const double pixelsPerWheelDelta = 48.0 / 120.0;
        var currentOffset = state.ScrollViewer.VerticalOffset;
        var targetOffset = currentOffset - e.Delta * pixelsPerWheelDelta;
        state.ScrollViewer.ScrollToVerticalOffset(targetOffset);
        SmartConLogger.Debug($"Wheel forwarded: currentOffset={currentOffset.ToString("F0", CultureInfo.InvariantCulture)} targetOffset={targetOffset.ToString("F0", CultureInfo.InvariantCulture)}");
        e.Handled = true;
    }

    private static void Attach(TreeView tree)
    {
        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(Attach)));

        if (_states.ContainsKey(tree))
        {
            SmartConLogger.Debug("TreeView already attached");
            return;
        }

        var state = new StickyState(tree);
        _states[tree] = state;
        SmartConLogger.Debug($"TreeView attached, IsLoaded={tree.IsLoaded}");

        if (tree.IsLoaded)
            HookEvents(state);
        else
            tree.Loaded += OnTreeViewLoaded;

        tree.Unloaded += OnTreeViewUnloaded;
    }

    private static void Detach(TreeView tree)
    {
        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(Detach)));

        if (!_states.TryGetValue(tree, out var state))
        {
            SmartConLogger.Debug("TreeView was not attached");
            return;
        }

        UnhookEvents(state);
        tree.Unloaded -= OnTreeViewUnloaded;

        if (state.OverlayBorder is not null)
            state.OverlayBorder.PreviewMouseWheel -= OnOverlayPreviewMouseWheel;

        ClearOverlay(state);
        _states.Remove(tree);
        SmartConLogger.Debug("TreeView detached");
    }

    private static void OnTreeViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeView tree) return;
        if (!_states.TryGetValue(tree, out var state)) return;

        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(OnTreeViewLoaded)));

        HookEvents(state);
        tree.Loaded -= OnTreeViewLoaded;
    }

    private static void OnTreeViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeView tree) return;

        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(OnTreeViewUnloaded)));

        Detach(tree);
    }

    private static void HookEvents(StickyState state)
    {
        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(HookEvents)));

        var scrollViewer = FindVisualChild<ScrollViewer>(state.Tree);
        SmartConLogger.Debug($"FindVisualChild<ScrollViewer>={scrollViewer is not null}");
        if (scrollViewer is null) return;

        state.ScrollViewer = scrollViewer;
        scrollViewer.ScrollChanged += OnScrollChanged;
        state.Tree.LayoutUpdated += OnLayoutUpdated;

        UpdateOverlayBorder(state);

        state.Attached = true;
        RecalculateSticky(state);
    }

    private static void UnhookEvents(StickyState state)
    {
        using var _scope = SmartConLogger.BeginScope("StickyHeader",
            ("Method", nameof(UnhookEvents)));

        if (state.ScrollViewer is not null)
            state.ScrollViewer.ScrollChanged -= OnScrollChanged;

        state.Tree.LayoutUpdated -= OnLayoutUpdated;

        state.ScrollViewer = null;
        state.Attached = false;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var state = FindStateByScrollViewer(sender);
        if (state is null) return;

        RecalculateSticky(state);
    }

    private static StickyState? FindStateByScrollViewer(object sender)
    {
        if (sender is not ScrollViewer sv) return null;
        var tree = FindOwningTree(sv);
        if (tree is null) return null;
        return _states.TryGetValue(tree, out var state) ? state : null;
    }

    private static TreeView? FindOwningTree(ScrollViewer sv)
    {
        DependencyObject? current = sv;
        while (current is not null)
        {
            if (current is TreeView tv) return tv;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not TreeView tree) return;
        if (!_states.TryGetValue(tree, out var state)) return;
        if (!state.Attached) return;

        RecalculateSticky(state);
    }

    // ─── Core logic ────────────────────────────────────────────────────────

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

    /// <summary>
    /// Создаёт Border для overlay-стека. Визуально похож на оригинальный Category-header:
    /// иконка папки + имя + счётчик семейств, с отступом для индикации вложенности.
    /// Если <paramref name="originalTvi"/> задан — клик проксируется в оригинальный
    /// TreeViewItem (Select + BringIntoView).
    /// </summary>
    private static Border CreateOverlayItem(
        CategoryNodeViewModel cat,
        TreeViewItem? originalTvi,
        int depth,
        StickyState state,
        Style? overlayStyle,
        Geometry? folderGeometry,
        Brush? textMuted,
        Brush? backgroundBrush)
    {
        // В дереве отступ текста категории depth d = 44 + 20*d:
        // Border.Padding(4) + ToggleButton(16) + ToggleButton.Margin(4) +
        // FolderIcon(14) + FolderIcon.Margin(6) = 44, плюс 20px за каждый уровень вложенности.
        // В overlay иконка папки занимает 20px (14 + margin 6), поэтому padding left
        // должен быть 24 + 20*d, чтобы текст совпадал с деревом.
        var leftPadding = 24 + depth * 20;

        var border = new Border
        {
            Style = overlayStyle,
            Background = backgroundBrush ?? Brushes.White,
            IsHitTestVisible = true,
            Padding = new Thickness(leftPadding, 2, 6, 2),
            Cursor = originalTvi is not null ? System.Windows.Input.Cursors.Hand : null,
            ToolTip = originalTvi is not null ? "Прокрутить к этой категории" : null,
        };

        // Кликабельность overlay-копии — проксируем клик в оригинальный TVI,
        // но сдвигаем скролл так, чтобы категория оказалась ПОД sticky-bar,
        // а не спряталась за ним.
        if (originalTvi is not null)
        {
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                originalTvi.IsSelected = true;
                ScrollCategoryBelowStickyBar(originalTvi, state);
            };
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Левая часть: иконка папки + имя категории.
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (folderGeometry is not null && textMuted is not null)
        {
            var path = new Path
            {
                Data = folderGeometry,
                Fill = textMuted,
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0),
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(path);
        }
        var text = new TextBlock
        {
            Text = cat.DisplayName,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(text);
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);

        // Правая часть: счётчик семейств.
        if (cat.FamilyCount > 0)
        {
            var count = new TextBlock
            {
                Text = $" ({cat.FamilyCount})",
                Foreground = textMuted ?? Brushes.Gray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            Grid.SetColumn(count, 1);
            grid.Children.Add(count);
        }

        border.Child = grid;
        return border;
    }

    // ─── Visual tree helpers ───────────────────────────────────────────────

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>
    /// Ищет <c>HeaderRow</c> в шаблоне TreeViewItem. Используется только для определения
    /// позиции header относительно ScrollContentPresenter (для расчёта current category).
    /// Сам визуал не используется — overlay рендерит собственные Border'ы.
    /// </summary>
    private static FrameworkElement? FindHeaderRow(TreeViewItem tvi)
    {
        if (tvi.Template is null)
        {
            using var _scopeNull = SmartConLogger.BeginScope("StickyHeader", ("Method", nameof(FindHeaderRow)));
            SmartConLogger.Debug("TreeViewItem template is null");
            return null;
        }

        var header = tvi.Template.FindName("HeaderRow", tvi) as FrameworkElement;
        if (header is null)
        {
            using var _scopeMissing = SmartConLogger.BeginScope("StickyHeader", ("Method", nameof(FindHeaderRow)));
            SmartConLogger.Debug("HeaderRow not found in template");
        }
        return header;
    }

    private static double GetTopInScp(FrameworkElement element, ScrollContentPresenter scp)
    {
        try
        {
            var transform = element.TransformToAncestor(scp);
            var point = transform.Transform(new Point(0, 0));
            return point.Y;
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope("StickyHeader", ("Method", nameof(GetTopInScp)));
            SmartConLogger.Warn($"TransformToAncestor failed: {ex.GetType().Name}: {ex.Message} [Action: report scroll state in the sticky-line issue]");
            return double.PositiveInfinity;
        }
    }

    private static void ClearOverlay(StickyState state)
    {
        // Без собственного scope: вызывается на каждый тик скролла, пока стек
        // пуст. Переходы состояния логируются в LogStickyStateOnChange.
        GetOverlay(state.Tree)?.Children.Clear();
    }

    // ─── Per-TreeView state ─────────────────────────────────────────────────

    private sealed class StickyState
    {
        public StickyState(TreeView tree)
        {
            Tree = tree;
        }

        public TreeView Tree { get; }
        public ScrollViewer? ScrollViewer { get; set; }
        public Border? OverlayBorder { get; set; }
        public bool Attached { get; set; }

        // Диагностика: логируем только переходы состояния, не каждый тик скролла.
        public string? LastLoggedSignature { get; set; }
        public int LastLoggedTviCount { get; set; } = -1;
        public int LastLoggedVmCount { get; set; } = -1;
    }
}
