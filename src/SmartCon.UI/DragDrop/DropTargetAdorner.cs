using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SmartCon.UI.DragDrop;

/// <summary>
/// Adorner that draws a highlight rectangle around a potential drop target.
/// Added to the AdornerLayer of the TreeViewItem being dragged over.
/// </summary>
public sealed class DropTargetAdorner : Adorner
{
    private static readonly Brush DefaultBackgroundBrush;
    private static readonly Brush DefaultBorderBrush;
    private const double CornerRadius = 4.0;
    private const double BorderThickness = 1.0;
    private const double BackgroundOpacity = 0.15;
    
    static DropTargetAdorner()
    {
        // Try to resolve theme brushes; fall back to hard-coded values if resources unavailable
        DefaultBackgroundBrush = new SolidColorBrush(Color.FromArgb(38, 41, 121, 255));  // ~15% Accent blue
        DefaultBackgroundBrush.Freeze();
        
        DefaultBorderBrush = new SolidColorBrush(Color.FromArgb(128, 41, 121, 255));  // 50% Accent blue
        DefaultBorderBrush.Freeze();
    }
    
    public DropTargetAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;  // CRITICAL: does not intercept mouse/drop events
    }
    
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (AdornedElement is not FrameworkElement element)
            return;
            
        // Use ActualSize for the element's bounds
        var rect = new Rect(element.RenderSize);
        
        // Background fill with rounded corners
        var backgroundPen = new Pen(DefaultBackgroundBrush, 0);
        drawingContext.DrawRoundedRectangle(
            DefaultBackgroundBrush,
            backgroundPen,
            rect,
            CornerRadius,
            CornerRadius);
        
        // Border stroke with rounded corners
        var borderPen = new Pen(DefaultBorderBrush, BorderThickness);
        drawingContext.DrawRoundedRectangle(
            null,
            borderPen,
            rect,
            CornerRadius,
            CornerRadius);
    }
}
