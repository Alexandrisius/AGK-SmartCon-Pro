using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Attached behavior that preserves a multi-row <see cref="DataGrid"/>
/// selection when the user clicks an inline editor (ComboBox, Button,
/// CheckBox, TextBox) inside an already-selected cell.
///
/// Background
/// ──────────
/// In <c>SelectionMode="Extended"</c> + <c>SelectionUnit="FullRow"</c>,
/// the DataGrid's internal <c>HandleSelectionForCellInput</c> collapses
/// <c>SelectedItems</c> to the clicked row whenever the user clicks a
/// cell, which prevents the "change Action / Category once and have it
/// apply to all selected rows" UX.
///
/// Approaches tried and why they failed
/// ─────────────────────────────────────
/// <list type="number">
///   <item>
///     <c>e.Handled = true</c> on
///     <c>DataGridCell.PreviewMouseLeftButtonDown</c> when the cell is
///     already selected and no modifier is pressed. Worked for plain
///     TextBlock cells but <b>blocked the ComboBox / Button inside the
///     cell from receiving its bubbling <c>MouseLeftButtonDown</c></b>,
///     so the user could not open the dropdown or activate the button
///     without losing the multi-selection again. Discarded.
///   </item>
///   <item>
///     <c>SelectionMode="Single"</c> + a hand-rolled
///     <c>DataGridRow.PreviewMouseLeftButtonDown</c> handler that wrote
///     <c>IsSelected</c> on the row VMs via reflection and called
///     <c>e.Handled = true</c> to suppress the DataGrid's own
///     selection logic. Failed because <c>SelectionMode="Single"</c>
///     physically allows only one <c>IsSelected = true</c> row at a
///     time — the DataGrid always collapses other rows when a new row
///     is selected, no matter what the VM does. Discarded.
///   </item>
///   <item>
///     <c>SelectionMode="Single"</c> with the row chrome clicks
///     delegated to the behavior but inline editors (ComboBox / Button)
///     left untouched so the editor receives its bubbling event
///     naturally. Ctrl+Click then stopped working because the
///     DataGrid was removed from the selection loop entirely, and our
///     reflection-based update could not reproduce the native Ctrl+Click
///     toggle UX reliably. Discarded.
/// </list>
///
/// Final approach
/// ───────────────
/// <list type="bullet">
///   <item>
///     Keep <c>SelectionMode="Extended"</c> so the DataGrid itself
///     handles all Ctrl/Shift/Click selection.
///   </item>
///   <item>
///     Bind <c>DataGridRow.IsSelected</c> TwoWay to a VM-owned
///     <c>IsSelected</c> on each row so visual selection is a
///     first-class VM concept.
///   </item>
///   <item>
///     When the user clicks an already-selected cell without modifiers
///     and more than one row is selected, snapshot
///     <c>SelectedItems</c> and restore it in
///     <c>SelectionChanged</c> if the DataGrid collapsed the
///     selection. Do <b>not</b> set <c>e.Handled = true</c> so the
///     ComboBox / Button still gets its bubbling
///     <c>MouseLeftButtonDown</c> and opens the dropdown / activates
///     the button.
///   </item>
/// </list>
///
/// Required XAML pattern:
/// <code>
/// &lt;DataGrid SelectionMode="Extended"
///           SelectionUnit="FullRow"
///           behaviors:DataGridBehaviors.RestoreMultiSelectOnCellClick="True"&gt;
///     &lt;DataGrid.RowStyle&gt;
///         &lt;Style TargetType="DataGridRow" BasedOn="{StaticResource ...}"&gt;
///             &lt;Setter Property="IsSelected"
///                     Value="{Binding IsSelected, Mode=TwoWay}"/&gt;
///         &lt;/Style&gt;
///     &lt;/DataGrid.RowStyle&gt;
/// &lt;/DataGrid&gt;
/// </code>
/// </summary>
public static partial class DataGridBehaviors
{
    private static readonly DependencyProperty PendingRestoreProperty =
        DependencyProperty.RegisterAttached(
            "PendingRestore",
            typeof(List<object>),
            typeof(DataGridBehaviors));

    private static List<object>? GetPendingRestore(DataGrid dataGrid) =>
        (List<object>?)dataGrid.GetValue(PendingRestoreProperty);

    private static void SetPendingRestore(DataGrid dataGrid, List<object>? value) =>
        dataGrid.SetValue(PendingRestoreProperty, value);

    public static readonly DependencyProperty RestoreMultiSelectOnCellClickProperty =
        DependencyProperty.RegisterAttached(
            "RestoreMultiSelectOnCellClick",
            typeof(bool),
            typeof(DataGridBehaviors),
            new PropertyMetadata(false, OnRestoreMultiSelectOnCellClickChanged));

    public static bool GetRestoreMultiSelectOnCellClick(DependencyObject obj) =>
        (bool)obj.GetValue(RestoreMultiSelectOnCellClickProperty);

    public static void SetRestoreMultiSelectOnCellClick(DependencyObject obj, bool value) =>
        obj.SetValue(RestoreMultiSelectOnCellClickProperty, value);

    private static void OnRestoreMultiSelectOnCellClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid dataGrid) return;

        dataGrid.PreviewMouseLeftButtonDown -= OnDataGridPreviewMouseLeftButtonDownRestore;
        dataGrid.SelectionChanged -= OnDataGridSelectionChangedRestore;

        if ((bool)e.NewValue)
        {
            dataGrid.PreviewMouseLeftButtonDown += OnDataGridPreviewMouseLeftButtonDownRestore;
            dataGrid.SelectionChanged += OnDataGridSelectionChangedRestore;
        }
        else
        {
            SetPendingRestore(dataGrid, null);
        }
    }

    private static void OnDataGridPreviewMouseLeftButtonDownRestore(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid) return;
        if (e.OriginalSource is not DependencyObject source) return;

        // Snapshot only on the exact scenario that triggers the collapse:
        // a plain click (no modifier) on a cell that is already part of
        // a multi-selection. Any other click — Ctrl+Click, Shift+Click,
        // click on an unselected cell, click on an empty area — leaves
        // the snapshot empty so the SelectionChanged handler does
        // nothing.
        var cell = FindAncestor<DataGridCell>(source);
        if (cell is null
            || !cell.IsSelected
            || Keyboard.Modifiers != ModifierKeys.None
            || dataGrid.SelectedItems.Count <= 1)
        {
            SetPendingRestore(dataGrid, null);
            return;
        }

        // We do NOT set e.Handled — the click must reach the inline
        // editor via the bubbling MouseLeftButtonDown.
        SetPendingRestore(dataGrid,
            new List<object>(dataGrid.SelectedItems.Cast<object>()));
    }

    private static void OnDataGridSelectionChangedRestore(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid dataGrid) return;
        var pending = GetPendingRestore(dataGrid);
        if (pending is null || pending.Count == 0) return;

        // Detect a real collapse: the new selection must be a strict
        // subset of the snapshot (same elements, minus one or more).
        // If the user toggled a row via Ctrl+Click, the snapshot is
        // never set, so we never get here. If the user changed the
        // selection with Shift+Click, the snapshot is cleared in
        // PreviewMouseLeftButtonDown (Modifiers != None).
        if (!IsStrictSubset(dataGrid.SelectedItems, pending))
        {
            SetPendingRestore(dataGrid, null);
            return;
        }

        // Copy first, then clear the pending state: a subsequent click
        // could legitimately overwrite PendingRestore before the
        // dispatcher callback runs.
        var toRestore = pending;
        SetPendingRestore(dataGrid, null);

        // DispatcherPriority.Input runs after the DataGrid finishes
        // processing the current SelectionChanged, but before the next
        // render pass, so the user does not see a flicker of the
        // collapsed selection.
        dataGrid.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!dataGrid.IsLoaded) return;

            // Bail if the user already initiated a new click that
            // legitimately re-populated the pending state.
            if (GetPendingRestore(dataGrid) is not null) return;

            foreach (var item in toRestore)
            {
                if (!dataGrid.SelectedItems.Contains(item))
                {
                    dataGrid.SelectedItems.Add(item);
                }
            }
        }), DispatcherPriority.Input);
    }

    private static bool IsStrictSubset(IList current, List<object> snapshot)
    {
        // current must have strictly fewer items than snapshot, and
        // every item in current must also be in snapshot. This is the
        // signature of "DataGrid collapsed a multi-selection by removing
        // items"; a legitimate Ctrl+Click would change the items
        // themselves, not just shrink the count, so it would fail this
        // check.
        if (current.Count >= snapshot.Count) return false;
        foreach (var item in current)
        {
            if (!snapshot.Contains(item)) return false;
        }
        return true;
    }
}
