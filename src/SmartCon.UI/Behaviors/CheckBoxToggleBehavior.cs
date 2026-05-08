using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Attached behavior that routes a CheckBox toggle through an ICommand before
/// the visual state changes.  If the command returns <c>false</c> or does not
/// execute, the click is cancelled and the CheckBox stays in its previous state.
/// </summary>
public static class CheckBoxToggleBehavior
{
    public static readonly DependencyProperty ToggleCommandProperty =
        DependencyProperty.RegisterAttached(
            "ToggleCommand",
            typeof(ICommand),
            typeof(CheckBoxToggleBehavior),
            new PropertyMetadata(null, OnToggleCommandChanged));

    public static readonly DependencyProperty ToggleCommandParameterProperty =
        DependencyProperty.RegisterAttached(
            "ToggleCommandParameter",
            typeof(object),
            typeof(CheckBoxToggleBehavior),
            new PropertyMetadata(null));

    public static ICommand? GetToggleCommand(DependencyObject obj)
        => (ICommand?)obj.GetValue(ToggleCommandProperty);

    public static void SetToggleCommand(DependencyObject obj, ICommand? value)
        => obj.SetValue(ToggleCommandProperty, value);

    public static object? GetToggleCommandParameter(DependencyObject obj)
        => obj.GetValue(ToggleCommandParameterProperty);

    public static void SetToggleCommandParameter(DependencyObject obj, object? value)
        => obj.SetValue(ToggleCommandParameterProperty, value);

    private static void OnToggleCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not CheckBox checkBox) return;

        checkBox.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        checkBox.KeyDown -= OnKeyDown;

        if (e.NewValue is not null)
        {
            checkBox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            checkBox.KeyDown += OnKeyDown;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox checkBox) return;

        var command = GetToggleCommand(checkBox);
        if (command is null) return;

        var parameter = GetToggleCommandParameter(checkBox);
        bool newValue = checkBox.IsChecked != true;

        var payload = new CheckBoxTogglePayload(parameter, newValue);

        if (!command.CanExecute(payload))
        {
            e.Handled = true;
            return;
        }

        command.Execute(payload);
        e.Handled = true;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        if (sender is not CheckBox checkBox) return;

        var command = GetToggleCommand(checkBox);
        if (command is null) return;

        var parameter = GetToggleCommandParameter(checkBox);
        bool newValue = checkBox.IsChecked != true;

        var payload = new CheckBoxTogglePayload(parameter, newValue);

        if (!command.CanExecute(payload))
        {
            e.Handled = true;
            return;
        }

        command.Execute(payload);
        e.Handled = true;
    }

}

/// <summary>
/// Payload passed to the ToggleCommand.
/// </summary>
public sealed record CheckBoxTogglePayload(object? Parameter, bool NewValue);
