using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Attached behavior for DataGridCell: starts cell editing on the first
/// left-click instead of the default "click current cell again" sequence
/// (WPF CodePlex single-click editing pattern). Applied per-column via
/// DataGridColumn.CellStyle so non-editable columns are unaffected.
/// </summary>
public static partial class DataGridBehaviors
{
    public static readonly DependencyProperty SingleClickEditProperty =
        DependencyProperty.RegisterAttached(
            "SingleClickEdit",
            typeof(bool),
            typeof(DataGridBehaviors),
            new PropertyMetadata(false, OnSingleClickEditChanged));

    public static bool GetSingleClickEdit(DependencyObject obj)
        => (bool)obj.GetValue(SingleClickEditProperty);

    public static void SetSingleClickEdit(DependencyObject obj, bool value)
        => obj.SetValue(SingleClickEditProperty, value);

    private static void OnSingleClickEditChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGridCell cell)
            return;

        if ((bool)e.NewValue)
            cell.PreviewMouseLeftButtonDown += OnSingleClickEditMouseDown;
        else
            cell.PreviewMouseLeftButtonDown -= OnSingleClickEditMouseDown;
    }

    private static void OnSingleClickEditMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell || cell.IsEditing || cell.IsReadOnly)
            return;

        if (!cell.IsFocused)
            cell.Focus();

        FindVisualParentOfType<DataGrid>(cell)?.BeginEdit(e);
    }

    private static T? FindVisualParentOfType<T>(DependencyObject element) where T : DependencyObject
    {
        var parent = element;
        while (parent is not null)
        {
            if (parent is T typed)
                return typed;
            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }
}
