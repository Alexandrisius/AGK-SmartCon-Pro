using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Attached property that re-evaluates <c>MenuItem.Command.CanExecute</c>
/// whenever <c>MenuItem.CommandParameter</c> changes. This is the local
/// equivalent of <c>dotnet/wpf#4217</c> (merged 2022-07-21 into .NET Core
/// 3.1+ / .NET 5+ / .NET 8+) for .NET Framework 4.x, which never received
/// the upstream fix.
///
/// Background (verified by Exa on 2026-06-19, see
/// <c>docs/adr/028-stale-check-menuitem.md</c> for the writeup):
///   - When a <see cref="ContextMenu"/> is opened, WPF sets the
///     <see cref="MenuItem"/>'s properties in XAML order. If
///     <c>Command</c> is set before <c>CommandParameter</c>, the
///     <see cref="System.Windows.Input.ICommand.CanExecute"/> callback
///     fires with a <c>null</c> parameter because the binding has not
///     yet resolved.
///   - .NET Core 3.1+ WPF added a <c>PropertyChangedCallback</c> on
///     <see cref="MenuItem.CommandParameterProperty"/> that re-invokes
///     <c>UpdateCanExecute</c> when the parameter changes; .NET
///     Framework 4.x does not have that callback.
///   - <c>CommandManager.InvalidateRequerySuggested()</c> does NOT help
///     for already-shown <see cref="ContextMenu"/> items because
///     <see cref="ContextMenu"/> caches <see cref="MenuItem"/> instances
///     and stops re-evaluating <c>CanExecute</c> after the first show
///     (see StackOverflow 37988297 "CanExecute not raised when context
///     menu opens").
///   - <c>MenuItem.UpdateCanExecute</c> is <c>internal</c> in WPF, so
///     subclassing from outside the WPF assembly cannot call it
///     directly.
///
/// Workaround: when <c>CommandParameter</c> changes, briefly null the
/// <see cref="MenuItem.Command"/> and re-assign it. This triggers the
/// public <c>OnCommandChanged</c> hook which calls
/// <c>UpdateCanExecute</c> internally - identical to the
/// <c>PropertyChangedCallback</c> that PR #4217 added upstream.
///
/// Important: the subscription is intentionally NOT torn down on
/// <c>MenuItem.Unloaded</c>. WPF reuses <see cref="MenuItem"/>
/// instances across <see cref="ContextMenu"/> shows, but the
/// <c>RequeryOnChange</c> attached property only fires its
/// <see cref="PropertyMetadata.PropertyChangedCallback"/> when the
/// value actually changes. If we unsubscribe on <c>Unloaded</c>, the
/// handler is gone for every subsequent ContextMenu show on the same
/// MenuItem instance and <c>CanExecute</c> stays stale on .NET
/// Framework 4.x (which lacks the upstream dotnet/wpf#4217 fix).
/// See Issue #78 for the 2026-06-22 regression introduced in commit
/// 8ffe965, where a temporary <c>OnMenuItemUnloaded</c> cleanup
/// silently disabled the workaround after the first ContextMenu close.
/// </summary>
public static class MenuItemCommandParameterRequery
{
    public static readonly DependencyProperty RequeryOnChangeProperty =
        DependencyProperty.RegisterAttached(
            "RequeryOnChange",
            typeof(bool),
            typeof(MenuItemCommandParameterRequery),
            new PropertyMetadata(false, OnRequeryOnChangeChanged));

    public static void SetRequeryOnChange(DependencyObject d, bool value) =>
        d.SetValue(RequeryOnChangeProperty, value);

    public static bool GetRequeryOnChange(DependencyObject d) =>
        (bool)d.GetValue(RequeryOnChangeProperty);

    private static void OnRequeryOnChangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MenuItem menuItem) return;
        if ((bool)e.NewValue)
        {
            var dpd = DependencyPropertyDescriptor.FromProperty(
                MenuItem.CommandParameterProperty, typeof(MenuItem));
            dpd?.AddValueChanged(menuItem, OnCommandParameterChanged);
        }
    }

    private static void OnCommandParameterChanged(object? sender, EventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        var cmd = menuItem.Command;
        if (cmd is null) return;

        menuItem.Command = null;
        menuItem.Command = cmd;
    }
}
