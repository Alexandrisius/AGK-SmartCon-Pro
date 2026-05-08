using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SmartCon.UI.DragDrop;

/// <summary>
/// A popup that follows the mouse during a drag operation, showing a preview of the dragged item.
/// Based on GongSolutions.WPF.DragDrop approach (Popup instead of Adorner for better performance).
/// </summary>
public sealed class DragPreviewPopup : Popup
{
    private readonly TextBlock _textBlock;

    public DragPreviewPopup()
    {
        // Popup configuration
        AllowsTransparency = true;
        IsHitTestVisible = false;  // CRITICAL: does not intercept mouse events
        AllowDrop = false;
        StaysOpen = true;
        Placement = PlacementMode.RelativePoint;
        PlacementTarget = null;  // Will be set to Window.Content

        // Visual: Border + TextBlock
        var border = new Border
        {
            Background = Application.Current?.Resources["SurfaceBrush"] as Brush ?? Brushes.White,
            BorderBrush = Application.Current?.Resources["AccentBrush"] as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Opacity = 0.9,
            Effect = new DropShadowEffect
            {
                ShadowDepth = 2,
                BlurRadius = 4,
                Opacity = 0.3
            }
        };

        _textBlock = new TextBlock
        {
            Foreground = Application.Current?.Resources["TextPrimaryBrush"] as Brush ?? Brushes.Black,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300
        };

        border.Child = _textBlock;
        Child = border;
    }

    /// <summary>
    /// Sets the placement target (should be the window's root element).
    /// </summary>
    public void SetPlacementTarget(UIElement target)
    {
        PlacementTarget = target;
    }

    /// <summary>
    /// Updates the display text of the dragged item.
    /// </summary>
    public void SetText(string? text)
    {
        _textBlock.Text = text ?? "...";
    }

    /// <summary>
    /// Moves the popup to the specified point relative to the placement target.
    /// </summary>
    public void Move(Point point)
    {
        // Offset so cursor is at the left-center of the popup
        var offsetX = point.X + 15;
        var offsetY = point.Y - 10;

        SetCurrentValue(HorizontalOffsetProperty, offsetX);
        SetCurrentValue(VerticalOffsetProperty, offsetY);
    }

    /// <summary>
    /// Shows the popup if not already open.
    /// </summary>
    public void Show()
    {
        if (!IsOpen)
            IsOpen = true;
    }

    /// <summary>
    /// Hides and disposes the popup.
    /// </summary>
    public void Hide()
    {
        IsOpen = false;
    }
}
