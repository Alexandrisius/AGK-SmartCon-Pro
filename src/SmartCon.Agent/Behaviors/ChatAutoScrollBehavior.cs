using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SmartCon.Agent.Behaviors;

public static class ChatAutoScrollBehavior
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.RegisterAttached(
            "ItemsSource",
            typeof(System.Collections.IEnumerable),
            typeof(ChatAutoScrollBehavior),
            new PropertyMetadata(null, OnItemsSourceChanged));

    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached(
            "Handler",
            typeof(NotifyCollectionChangedEventHandler),
            typeof(ChatAutoScrollBehavior),
            new PropertyMetadata(null));

    public static void SetItemsSource(DependencyObject element, System.Collections.IEnumerable? value) =>
        element.SetValue(ItemsSourceProperty, value);

    public static System.Collections.IEnumerable? GetItemsSource(DependencyObject element) =>
        (System.Collections.IEnumerable?)element.GetValue(ItemsSourceProperty);

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        if (e.OldValue is INotifyCollectionChanged oldSource
            && scrollViewer.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler oldHandler)
        {
            oldSource.CollectionChanged -= oldHandler;
            scrollViewer.ClearValue(HandlerProperty);
        }

        if (e.NewValue is INotifyCollectionChanged newSource)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) =>
                scrollViewer.Dispatcher.BeginInvoke(
                    new Action(() => scrollViewer.ScrollToEnd()),
                    DispatcherPriority.Background);

            newSource.CollectionChanged += handler;
            scrollViewer.SetValue(HandlerProperty, handler);
        }
    }
}