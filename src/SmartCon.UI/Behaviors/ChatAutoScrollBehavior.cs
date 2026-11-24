using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Keeps a chat transcript pinned to the live tail: a new item and streamed
/// text inside the existing items both scroll the list to the end. A reader
/// who scrolled away from the tail is left alone until a new item arrives.
/// </summary>
public static class ChatAutoScrollBehavior
{
    private const double TailTolerance = 2.0;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.RegisterAttached(
            "ItemsSource",
            typeof(IEnumerable),
            typeof(ChatAutoScrollBehavior),
            new PropertyMetadata(null, OnItemsSourceChanged));

    private static readonly DependencyProperty CollectionHandlerProperty =
        DependencyProperty.RegisterAttached(
            "CollectionHandler",
            typeof(NotifyCollectionChangedEventHandler),
            typeof(ChatAutoScrollBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty ItemHandlerProperty =
        DependencyProperty.RegisterAttached(
            "ItemHandler",
            typeof(PropertyChangedEventHandler),
            typeof(ChatAutoScrollBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty TrackedItemsProperty =
        DependencyProperty.RegisterAttached(
            "TrackedItems",
            typeof(List<INotifyPropertyChanged>),
            typeof(ChatAutoScrollBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty ScrollScheduledProperty =
        DependencyProperty.RegisterAttached(
            "ScrollScheduled",
            typeof(bool),
            typeof(ChatAutoScrollBehavior),
            new PropertyMetadata(false));

    public static void SetItemsSource(DependencyObject element, IEnumerable? value) =>
        element.SetValue(ItemsSourceProperty, value);

    public static IEnumerable? GetItemsSource(DependencyObject element) =>
        (IEnumerable?)element.GetValue(ItemsSourceProperty);

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        if (e.OldValue is INotifyCollectionChanged oldSource
            && scrollViewer.GetValue(CollectionHandlerProperty) is NotifyCollectionChangedEventHandler oldHandler)
        {
            oldSource.CollectionChanged -= oldHandler;
            scrollViewer.ClearValue(CollectionHandlerProperty);
        }
        DetachItems(scrollViewer);

        if (e.NewValue is not IEnumerable newSource || e.NewValue is not INotifyCollectionChanged newNotifier)
            return;

        NotifyCollectionChangedEventHandler collectionHandler = (_, _) =>
        {
            AttachItems(scrollViewer, newSource);
            ScheduleScroll(scrollViewer, force: true);
        };
        newNotifier.CollectionChanged += collectionHandler;
        scrollViewer.SetValue(CollectionHandlerProperty, collectionHandler);

        // The item type lives in a module the UI library does not reference, so the
        // streamed properties are matched by name.
        PropertyChangedEventHandler itemHandler = (_, args) =>
        {
            if (args.PropertyName is "Text" or "ReasoningText")
                ScheduleScroll(scrollViewer, force: false);
        };
        scrollViewer.SetValue(ItemHandlerProperty, itemHandler);
        AttachItems(scrollViewer, newSource);
    }

    private static void AttachItems(ScrollViewer scrollViewer, IEnumerable source)
    {
        DetachItems(scrollViewer);
        if (scrollViewer.GetValue(ItemHandlerProperty) is not PropertyChangedEventHandler handler)
            return;

        var tracked = new List<INotifyPropertyChanged>();
        foreach (var item in source)
        {
            if (item is not INotifyPropertyChanged notifier)
                continue;

            notifier.PropertyChanged += handler;
            tracked.Add(notifier);
        }
        scrollViewer.SetValue(TrackedItemsProperty, tracked);
    }

    private static void DetachItems(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(TrackedItemsProperty) is not List<INotifyPropertyChanged> tracked)
            return;

        if (scrollViewer.GetValue(ItemHandlerProperty) is PropertyChangedEventHandler handler)
        {
            foreach (var item in tracked)
                item.PropertyChanged -= handler;
        }
        tracked.Clear();
    }

    private static void ScheduleScroll(ScrollViewer scrollViewer, bool force)
    {
        if (!force && !IsAtTail(scrollViewer))
            return;

        if (scrollViewer.GetValue(ScrollScheduledProperty) is true)
            return;

        scrollViewer.SetValue(ScrollScheduledProperty, true);
        scrollViewer.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                scrollViewer.ClearValue(ScrollScheduledProperty);
                scrollViewer.ScrollToEnd();
            }),
            DispatcherPriority.Background);
    }

    private static bool IsAtTail(ScrollViewer scrollViewer) =>
        scrollViewer.ScrollableHeight <= 0
        || scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - TailTolerance;
}