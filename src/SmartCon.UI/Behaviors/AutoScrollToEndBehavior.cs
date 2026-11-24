using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Follows a growing text block while <c>Follow</c> is true: every change of
/// the bound <c>Text</c> scrolls the viewer to its last line. Used by the live
/// reasoning body, where the reader must see the current generation instead of
/// the start of the block. A completed block returns to its first line.
/// </summary>
public static class AutoScrollToEndBehavior
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(AutoScrollToEndBehavior),
            new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty FollowProperty =
        DependencyProperty.RegisterAttached(
            "Follow",
            typeof(bool),
            typeof(AutoScrollToEndBehavior),
            new PropertyMetadata(false, OnFollowChanged));

    private static readonly DependencyProperty ScrollScheduledProperty =
        DependencyProperty.RegisterAttached(
            "ScrollScheduled",
            typeof(bool),
            typeof(AutoScrollToEndBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty PendingToEndProperty =
        DependencyProperty.RegisterAttached(
            "PendingToEnd",
            typeof(bool),
            typeof(AutoScrollToEndBehavior),
            new PropertyMetadata(false));

    public static void SetText(DependencyObject element, string? value) =>
        element.SetValue(TextProperty, value);

    public static string? GetText(DependencyObject element) =>
        (string?)element.GetValue(TextProperty);

    public static void SetFollow(DependencyObject element, bool value) =>
        element.SetValue(FollowProperty, value);

    public static bool GetFollow(DependencyObject element) =>
        (bool)element.GetValue(FollowProperty);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer || e.NewValue is null)
            return;

        if (GetFollow(scrollViewer))
            ScheduleScroll(scrollViewer, toEnd: true);
    }

    private static void OnFollowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
            return;

        // A finished block starts at its first line when the reader opens it:
        // only the live stream is pinned to the tail.
        if (e.NewValue is true)
            ScheduleScroll(scrollViewer, toEnd: true);
        else if (e.OldValue is true)
            ScheduleScroll(scrollViewer, toEnd: false);
    }

    private static void ScheduleScroll(ScrollViewer scrollViewer, bool toEnd)
    {
        // The last request wins when a delta and the end of the stream arrive
        // before the dispatcher runs the scheduled scroll.
        scrollViewer.SetValue(PendingToEndProperty, toEnd);
        if (scrollViewer.GetValue(ScrollScheduledProperty) is true)
            return;

        scrollViewer.SetValue(ScrollScheduledProperty, true);
        scrollViewer.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                scrollViewer.ClearValue(ScrollScheduledProperty);
                if (scrollViewer.GetValue(PendingToEndProperty) is true)
                    scrollViewer.ScrollToEnd();
                else
                    scrollViewer.ScrollToTop();
            }),
            DispatcherPriority.Background);
    }
}