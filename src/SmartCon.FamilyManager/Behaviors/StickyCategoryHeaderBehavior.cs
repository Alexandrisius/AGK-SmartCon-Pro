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
public static partial class StickyCategoryHeaderBehavior
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
