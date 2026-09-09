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
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0
            && state.ScrollViewer.ExtentWidth > state.ScrollViewer.ViewportWidth)
        {
            state.ScrollViewer.ScrollToHorizontalOffset(state.ScrollViewer.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        const double pixelsPerWheelDelta = 48.0 / 120.0;
        var currentOffset = state.ScrollViewer.VerticalOffset;
        var targetOffset = currentOffset - e.Delta * pixelsPerWheelDelta;
        state.ScrollViewer.ScrollToVerticalOffset(targetOffset);
        SmartConLogger.Debug($"Wheel forwarded: currentOffset={currentOffset.ToString("F0", CultureInfo.InvariantCulture)} targetOffset={targetOffset.ToString("F0", CultureInfo.InvariantCulture)}");
        e.Handled = true;
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
}
