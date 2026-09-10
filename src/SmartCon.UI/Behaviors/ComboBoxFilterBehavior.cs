using System.Windows;
using System.Windows.Controls;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Attached behavior for an editable ComboBox: filters the dropdown items
/// (string ItemsSource) with case-insensitive Contains while the user types.
/// Revit-safe: no Application.Current dependency, uses only control events.
///
/// Known WPF quirk handled here: opening the dropdown of an editable ComboBox
/// selects all text in PART_EditableTextBox, which makes the next keystroke
/// overwrite everything the user typed (dotnet/wpf known behavior). We reset
/// the selection to the end of the text on DropDownOpened.
/// </summary>
public static class ComboBoxFilterBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ComboBoxFilterBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj)
        => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value)
        => obj.SetValue(IsEnabledProperty, value);

    /// <summary>
    /// When true, moves keyboard focus into the editable TextBox and opens the
    /// dropdown once the ComboBox is loaded. Intended for DataGrid editing
    /// templates so a single click puts the caret into the text field.
    /// </summary>
    public static readonly DependencyProperty FocusOnLoadedProperty =
        DependencyProperty.RegisterAttached(
            "FocusOnLoaded",
            typeof(bool),
            typeof(ComboBoxFilterBehavior),
            new PropertyMetadata(false));

    public static bool GetFocusOnLoaded(DependencyObject obj)
        => (bool)obj.GetValue(FocusOnLoadedProperty);

    public static void SetFocusOnLoaded(DependencyObject obj, bool value)
        => obj.SetValue(FocusOnLoadedProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo || !combo.IsEditable)
            return;

        if ((bool)e.NewValue)
        {
            if (combo.IsLoaded)
                Hook(combo);
            else
                combo.Loaded += OnLoaded;
        }
        else
        {
            Unhook(combo);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            combo.Loaded -= OnLoaded;
            Hook(combo);

            if (GetFocusOnLoaded(combo)
                && combo.Template.FindName("PART_EditableTextBox", combo) is TextBox textBox)
            {
                textBox.Focus();
                textBox.Select(textBox.Text.Length, 0);
                combo.IsDropDownOpen = true;
            }
        }
    }

    private static void Hook(ComboBox combo)
    {
        combo.ApplyTemplate();
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox textBox)
        {
            textBox.TextChanged -= OnTextChanged;
            textBox.TextChanged += OnTextChanged;
        }

        combo.DropDownOpened -= OnDropDownOpened;
        combo.DropDownOpened += OnDropDownOpened;
        combo.DropDownClosed -= OnDropDownClosed;
        combo.DropDownClosed += OnDropDownClosed;
        combo.Unloaded -= OnUnloaded;
        combo.Unloaded += OnUnloaded;
    }

    private static void Unhook(ComboBox combo)
    {
        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox textBox)
            textBox.TextChanged -= OnTextChanged;

        combo.DropDownOpened -= OnDropDownOpened;
        combo.DropDownClosed -= OnDropDownClosed;
        combo.Unloaded -= OnUnloaded;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
            Unhook(combo);
    }

    private static void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo)
            return;

        if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox textBox
            && textBox.SelectionLength > 0)
        {
            textBox.Select(textBox.Text.Length, 0);
        }
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo)
            combo.Items.Filter = null;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;
        if (textBox.TemplatedParent is not ComboBox combo)
            return;

        var text = textBox.Text;

        // Selection echo: picking an item rewrites Text to the item value.
        // Do not filter — otherwise the list collapses to the single match.
        if (combo.SelectedItem is string selected
            && string.Equals(selected, text, StringComparison.Ordinal))
        {
            combo.Items.Filter = null;
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            combo.Items.Filter = null;
        }
        else
        {
            combo.Items.Filter = item =>
                item is string s && ContainsIgnoreCase(s, text);
        }

        if (textBox.IsKeyboardFocusWithin && !combo.IsDropDownOpen)
            combo.IsDropDownOpen = true;
    }

    private static bool ContainsIgnoreCase(string haystack, string needle)
    {
#if NETFRAMEWORK
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#else
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#endif
    }
}
