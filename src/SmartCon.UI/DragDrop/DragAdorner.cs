using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SmartCon.UI.DragDrop;

/// <summary>
/// Adorner that follows the mouse cursor during a drag operation.
/// Added to the AdornerLayer of the window root element.
/// </summary>
public sealed class DragAdorner : Adorner
{
    private readonly Border _visual;
    private readonly TextBlock _textBlock;
    private Point _position;
    
    public DragAdorner(UIElement adornedElement, string? displayText) : base(adornedElement)
    {
        IsHitTestVisible = false;  // CRITICAL: does not intercept mouse/drop events
        
        _textBlock = new TextBlock
        {
            Text = displayText ?? "...",
            Foreground = Application.Current?.Resources["TextPrimaryBrush"] as Brush ?? Brushes.Black,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300
        };
        
        _visual = new Border
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
            },
            Child = _textBlock,
            Width = 200,  // Fixed width for consistent look
            Height = 30   // Fixed height
        };
        
        AddVisualChild(_visual);
        AddLogicalChild(_visual);
    }
    
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;
    
    protected override Size MeasureOverride(Size constraint)
    {
        _visual.Measure(constraint);
        return _visual.DesiredSize;
    }
    
    protected override Size ArrangeOverride(Size finalSize)
    {
        _visual.Arrange(new Rect(finalSize));
        return finalSize;
    }
    
    /// <summary>
    /// Updates the adorner position to follow the mouse.
    /// Call this from GiveFeedback event handler.
    /// </summary>
    public void UpdatePosition(Point position)
    {
        _position = position;
        InvalidateVisual();
    }
    
    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        var result = new GeneralTransformGroup();
        result.Children.Add(base.GetDesiredTransform(transform));
        
        // Offset so popup appears slightly below and to the right of cursor
        var offsetX = _position.X + 15;
        var offsetY = _position.Y + 15;
        
        result.Children.Add(new TranslateTransform(offsetX, offsetY));
        return result;
    }
}
