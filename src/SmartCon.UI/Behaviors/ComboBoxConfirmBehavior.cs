using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartCon.Core.Services;

namespace SmartCon.UI.Behaviors;

public static class ComboBoxConfirmBehavior
{
    public static readonly DependencyProperty IsConfirmationEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsConfirmationEnabled",
            typeof(bool),
            typeof(ComboBoxConfirmBehavior),
            new PropertyMetadata(false, OnIsConfirmationEnabledChanged));

    public static bool GetIsConfirmationEnabled(DependencyObject obj)
        => (bool)obj.GetValue(IsConfirmationEnabledProperty);

    public static void SetIsConfirmationEnabled(DependencyObject obj, bool value)
        => obj.SetValue(IsConfirmationEnabledProperty, value);

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(ComboBoxConfirmBehavior),
            new PropertyMetadata(null));

    public static ICommand? GetCommand(DependencyObject obj)
        => (ICommand?)obj.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject obj, ICommand? value)
        => obj.SetValue(CommandProperty, value);

    public static readonly DependencyProperty ConfirmTitleProperty =
        DependencyProperty.RegisterAttached(
            "ConfirmTitle",
            typeof(string),
            typeof(ComboBoxConfirmBehavior),
            new PropertyMetadata("Confirm"));

    public static string GetConfirmTitle(DependencyObject obj)
        => (string)obj.GetValue(ConfirmTitleProperty);

    public static void SetConfirmTitle(DependencyObject obj, string value)
        => obj.SetValue(ConfirmTitleProperty, value);

    public static readonly DependencyProperty ConfirmMessageProperty =
        DependencyProperty.RegisterAttached(
            "ConfirmMessage",
            typeof(string),
            typeof(ComboBoxConfirmBehavior),
            new PropertyMetadata("Are you sure?"));

    public static string GetConfirmMessage(DependencyObject obj)
        => (string)obj.GetValue(ConfirmMessageProperty);

    public static void SetConfirmMessage(DependencyObject obj, string value)
        => obj.SetValue(ConfirmMessageProperty, value);

    private static void OnIsConfirmationEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox comboBox) return;

        comboBox.SelectionChanged -= OnSelectionChanged;

        if ((bool)e.NewValue)
        {
            comboBox.Tag = false;
            comboBox.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox) return;
        if (comboBox.Tag is true) return;
        if (e.RemovedItems.Count == 0) return; // ignore initialization

        var bindingExpression = comboBox.GetBindingExpression(ComboBox.SelectedItemProperty);
        if (bindingExpression is null) return;

        var title = GetConfirmTitle(comboBox);
        var message = GetConfirmMessage(comboBox);

        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            bindingExpression.UpdateSource();

            var command = GetCommand(comboBox);
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
        }
        else
        {
            comboBox.Tag = true;
            bindingExpression.UpdateTarget();
            comboBox.Tag = false;
        }
    }
}
