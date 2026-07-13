using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SmartCon.UI.Behaviors;

public static class DataGridEditNewRowBehavior
{
    public static readonly DependencyProperty FocusItemProperty = DependencyProperty.RegisterAttached(
        "FocusItem",
        typeof(object),
        typeof(DataGridEditNewRowBehavior),
        new PropertyMetadata(null, OnFocusItemChanged));

    public static object GetFocusItem(DependencyObject obj)
        => obj.GetValue(FocusItemProperty);

    public static void SetFocusItem(DependencyObject obj, object value)
        => obj.SetValue(FocusItemProperty, value);

    private static void OnFocusItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid dataGrid || e.NewValue is null)
            return;

        var item = e.NewValue;

        dataGrid.Dispatcher.BeginInvoke(() =>
        {
            dataGrid.ScrollIntoView(item);
            dataGrid.UpdateLayout();

            if (dataGrid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
                return;

            if (GetCell(dataGrid, row, 0) is not DataGridCell cell)
                return;

            dataGrid.SelectedItem = item;
            dataGrid.CurrentCell = new DataGridCellInfo(cell);
            dataGrid.BeginEdit();

            var editCell = GetCell(dataGrid, row, 0);
            if (editCell?.Content is TextBox textBox)
            {
                Keyboard.Focus(textBox);
                textBox.SelectAll();
            }
            else if (FindVisualChild<TextBox>(editCell) is { } childTextBox)
            {
                Keyboard.Focus(childTextBox);
                childTextBox.SelectAll();
            }
            else
            {
                cell.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private static DataGridCell? GetCell(DataGrid dataGrid, DataGridRow row, int columnIndex)
    {
        if (row is null)
            return null;

        var presenter = FindVisualChild<DataGridCellsPresenter>(row);
        if (presenter is null)
        {
            row.ApplyTemplate();
            presenter = FindVisualChild<DataGridCellsPresenter>(row);
        }

        if (presenter is null)
            return null;

        var cell = (DataGridCell?)presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex);
        if (cell is null)
        {
            dataGrid.ScrollIntoView(row, dataGrid.Columns[columnIndex]);
            cell = (DataGridCell?)presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex);
        }

        return cell;
    }

    private static T? FindVisualChild<T>(Visual? parent) where T : Visual
    {
        if (parent is null)
            return null;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = (Visual?)VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }
}
