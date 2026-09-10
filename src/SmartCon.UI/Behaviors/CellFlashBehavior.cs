using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Issue #135 (P4): flashes the attached element's background when the
/// bound <see cref="FlashTokenProperty"/> increments. Used by the batch
/// import dialog to make silent automatic category changes visible
/// (the rename handler bumps the token; the VM never touches the view).
/// <para>
/// Design (Yellow Fade Technique, 37signals/Basecamp): a noticeable
/// hold phase (~0.5 s) followed by a slow fade (~1.5 s) — shorter
/// flashes are literally missed when the user is not staring at the
/// cell. <see cref="FillBehavior.Stop"/> returns the property to its
/// style-driven value after the animation, so no frozen/highlighted
/// state leaks into selection or theme brushes.
/// </para>
/// <para>
/// The token starts at 0 and the initial binding does NOT flash — only
/// real increments do. This avoids the classic
/// <c>Binding.TargetUpdated</c> pitfall where the whole column flashes
/// on initial load, sort and scroll (SO 10853845 / 14199022).
/// </para>
/// </summary>
public static class CellFlashBehavior
{
    /// <summary>Soft light blue — noticeable on the white dialog surface
    /// without the alarm connotation of the orange WarningBrush.</summary>
    private static readonly Color FlashAccent = Color.FromRgb(0xBB, 0xDE, 0xFB);

    private static readonly Duration HoldDuration = new(TimeSpan.FromMilliseconds(500));
    private static readonly TimeSpan FadeEnd = TimeSpan.FromMilliseconds(2000);

    public static readonly DependencyProperty FlashTokenProperty =
        DependencyProperty.RegisterAttached(
            "FlashToken",
            typeof(int),
            typeof(CellFlashBehavior),
            new PropertyMetadata(0, OnFlashTokenChanged));

    public static int GetFlashToken(DependencyObject obj)
        => (int)obj.GetValue(FlashTokenProperty);

    public static void SetFlashToken(DependencyObject obj, int value)
        => obj.SetValue(FlashTokenProperty, value);

    private static void OnFlashTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not int token || token <= 0) return;

        switch (d)
        {
            case Control control:
                Flash(control.Background, brush => control.Background = brush);
                break;
            case TextBlock textBlock:
                Flash(textBlock.Background, brush => textBlock.Background = brush);
                break;
        }
    }

    private static void Flash(System.Windows.Media.Brush? current, Action<System.Windows.Media.Brush> assign)
    {
        if (current is not SolidColorBrush brush || brush.IsFrozen)
        {
            brush = new SolidColorBrush(Colors.Transparent);
            assign(brush);
        }

        var animation = new ColorAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
            Duration = new Duration(FadeEnd),
        };
        animation.KeyFrames.Add(new LinearColorKeyFrame(FlashAccent, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearColorKeyFrame(FlashAccent, KeyTime.FromTimeSpan(HoldDuration.TimeSpan)));
        animation.KeyFrames.Add(new LinearColorKeyFrame(Colors.Transparent, KeyTime.FromTimeSpan(FadeEnd)));

        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
}
