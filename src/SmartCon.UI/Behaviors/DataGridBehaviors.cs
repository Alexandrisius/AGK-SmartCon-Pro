using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Attached behaviors for DataGrid controls.
/// </summary>
public static partial class DataGridBehaviors
{
    #region DeselectOnEmptyClick

    public static readonly DependencyProperty DeselectOnEmptyClickProperty =
        DependencyProperty.RegisterAttached(
            "DeselectOnEmptyClick",
            typeof(bool),
            typeof(DataGridBehaviors),
            new PropertyMetadata(false, OnDeselectOnEmptyClickChanged));

    public static bool GetDeselectOnEmptyClick(DependencyObject obj) => (bool)obj.GetValue(DeselectOnEmptyClickProperty);

    public static void SetDeselectOnEmptyClick(DependencyObject obj, bool value) => obj.SetValue(DeselectOnEmptyClickProperty, value);

    private static void OnDeselectOnEmptyClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid dataGrid) return;

        dataGrid.PreviewMouseLeftButtonDown -= OnDataGridPreviewMouseLeftButtonDown;

        if ((bool)e.NewValue)
        {
            dataGrid.PreviewMouseLeftButtonDown += OnDataGridPreviewMouseLeftButtonDown;
        }
    }

    private static void OnDataGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid) return;
        if (e.OriginalSource is not DependencyObject dep) return;

        // Check if click was on a DataGridRow or inside a cell
        var row = FindAncestor<DataGridRow>(dep);
        var cell = FindAncestor<DataGridCell>(dep);

        // If clicked on a row or cell, do nothing (allow normal selection)
        if (row is not null || cell is not null) return;

        // Clicked on empty area (header, scroll bar, or empty space below rows)
        // Clear selection
        dataGrid.SelectedItem = null;
        dataGrid.UnselectAll();
    }

    #endregion

    #region Helpers

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    #endregion
}
