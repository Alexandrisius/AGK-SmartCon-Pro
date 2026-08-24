using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SmartCon.UI.Controls;

/// <summary>
/// Editable ComboBox with type-to-filter autocomplete. Filtering goes
/// through <c>Items.Filter</c> (never an ItemsSource swap) and the
/// editable text box state is preserved around every filter refresh —
/// the naive TextBox + Popup(StaysOpen=false) composition breaks WPF
/// focus/mouse capture (dotnet/wpf#2166), which is why the framework
/// ComboBox (ToggleButton + own popup) is the only reliable host.
/// Pattern adapted from DotNetKit.Wpf.AutoCompleteComboBox
/// (github.com/vain0x/DotNetKit.Wpf.AutoCompleteComboBox).
/// Item display text is read via <see cref="TextSearch.TextPath"/>.
/// Requires <c>IsEditable=True</c> and a template with PART_EditableTextBox
/// (see CompactEditableComboBox in Generic.xaml).
/// </summary>
public class AutoCompleteComboBox : ComboBox
{
    private TextBox? _editableTextBox;
    private string? _previousText;
    private bool _openingFromTyping;

    public AutoCompleteComboBox()
    {
        IsEditable = true;
        IsTextSearchEnabled = false;
        StaysOpenOnEdit = true;
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnEditableTextChanged));
        PreviewKeyDown += OnPreviewKeyDown;
        DropDownOpened += OnDropDownOpened;
    }

    private TextBox? EditableTextBox
    {
        get
        {
            _editableTextBox ??= GetTemplateChild("PART_EditableTextBox") as TextBox;
            return _editableTextBox;
        }
    }

    /// <summary>Item text matched against the query (never null).</summary>
    private string TextFromItem(object? item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        var path = TextSearch.GetTextPath(this);
        if (string.IsNullOrEmpty(path))
        {
            return item.ToString() ?? string.Empty;
        }

        var getter = PropertyCache.GetOrAdd(
            (item.GetType(), path),
            static key => key.Item1.GetProperty(key.Item2));
        return getter?.GetValue(item)?.ToString() ?? string.Empty;
    }

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PropertyCache = new();

    /// <summary>
    /// Refreshing the view filter can empty/reset the editable text box
    /// (framework quirk) — save and restore its full state.
    /// </summary>
    private readonly struct TextBoxStatePreserver : IDisposable
    {
        private readonly TextBox _textBox;
        private readonly int _selectionStart;
        private readonly int _selectionLength;
        private readonly string _text;

        public TextBoxStatePreserver(TextBox textBox)
        {
            _textBox = textBox;
            _selectionStart = textBox.SelectionStart;
            _selectionLength = textBox.SelectionLength;
            _text = textBox.Text;
        }

        public void Dispose()
        {
            _textBox.Text = _text;
            _textBox.Select(_selectionStart, _selectionLength);
        }
    }

    private void UpdateFilter(Predicate<object>? filter)
    {
        var textBox = EditableTextBox;
        if (textBox == null)
        {
            Items.Filter = filter;
            return;
        }

        using (new TextBoxStatePreserver(textBox))
        using (Items.DeferRefresh())
        {
            Items.Filter = filter;
        }
    }

    /// <summary>Move the caret past the selection ComboBox leaves behind.</summary>
    private void Unselect()
    {
        var textBox = EditableTextBox;
        if (textBox == null)
        {
            return;
        }

        textBox.Select(textBox.SelectionStart + textBox.SelectionLength, 0);
    }

    private void OnEditableTextChanged(object sender, TextChangedEventArgs e)
    {
        // The routed handler sees every TextBox inside the template —
        // only the editable part is ours.
        var textBox = EditableTextBox;
        if (textBox == null || !ReferenceEquals(e.OriginalSource, textBox))
        {
            return;
        }

        var text = Text ?? string.Empty;
        if (text == _previousText)
        {
            return;
        }

        _previousText = text;

        // Programmatic Text updates (loaded value, picked label pushed by
        // the binding) must not pop the suggestion list.
        if (!IsKeyboardFocusWithin)
        {
            return;
        }

        if (SelectedItem != null && TextFromItem(SelectedItem) == text)
        {
            // The user just picked an item — the Text echo is not typing.
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            UpdateFilter(null);
            SelectedItem = null;
            OpenDropDown(fromTyping: true);
            return;
        }

        using (new TextBoxStatePreserver(textBox))
        {
            SelectedItem = null;
        }

        UpdateFilter(item =>
#if NET8_0_OR_GREATER
            TextFromItem(item).Contains(text, StringComparison.OrdinalIgnoreCase));
#else
            TextFromItem(item).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
#endif
        if (!Items.IsEmpty)
        {
            OpenDropDown(fromTyping: true);
        }

        Unselect();
    }

    private void OpenDropDown(bool fromTyping)
    {
        _openingFromTyping = fromTyping;
        IsDropDownOpen = true;
        _openingFromTyping = false;
    }

    private void OnDropDownOpened(object? sender, EventArgs e)
    {
        // User-initiated open (arrow button): show the full list, the
        // typed filter is stale by then. A typing-driven open keeps the
        // just-applied filter.
        if (!_openingFromTyping)
        {
            UpdateFilter(null);
        }

        // ComboBox selects the whole text when the dropdown opens — drop
        // the selection so the first keystroke doesn't wipe the label.
        Unselect();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Space: open the full list (matches common IDE autocomplete).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Space)
        {
            UpdateFilter(null);
            OpenDropDown(fromTyping: true);
            Unselect();
            e.Handled = true;
        }
    }
}
