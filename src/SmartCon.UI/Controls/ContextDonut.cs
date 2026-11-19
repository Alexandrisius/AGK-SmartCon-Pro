using System;
using System.Windows;
using System.Windows.Media;

namespace SmartCon.UI.Controls;

/// <summary>
/// Small circular context-usage indicator: a track ring plus a progress arc.
/// The percentage and the exact token numbers live in the tooltip owned by the
/// surrounding button, so the control stays a pure visual.
/// </summary>
public sealed class ContextDonut : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(ContextDonut),
        new FrameworkPropertyMetadata(
            0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(ContextDonut),
        new FrameworkPropertyMetadata(
            0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(ContextDonut),
        new FrameworkPropertyMetadata(
            Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush),
        typeof(Brush),
        typeof(ContextDonut),
        new FrameworkPropertyMetadata(
            Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarningBrushProperty = DependencyProperty.Register(
        nameof(WarningBrush),
        typeof(Brush),
        typeof(ContextDonut),
        new FrameworkPropertyMetadata(
            Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DangerBrushProperty = DependencyProperty.Register(
        nameof(DangerBrush),
        typeof(Brush),
        typeof(ContextDonut),
        new FrameworkPropertyMetadata(
            Brushes.Firebrick, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(ContextDonut),
        new FrameworkPropertyMetadata(
            3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush WarningBrush
    {
        get => (Brush)GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    public Brush DangerBrush
    {
        get => (Brush)GetValue(DangerBrushProperty);
        set => SetValue(DangerBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var size = Math.Min(ActualWidth, ActualHeight);
        var thickness = Math.Max(1, StrokeThickness);
        var radius = (size - thickness) / 2;
        if (radius <= 0)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(
            null, new Pen(TrackBrush, thickness), center, radius, radius);

        if (Maximum <= 0 || Value <= 0)
            return;

        var ratio = Math.Min(1d, Value / Maximum);
        if (ratio <= 0.0005)
            return;

        var brush = ratio >= 0.9
            ? DangerBrush
            : ratio >= 0.7
                ? WarningBrush
                : ProgressBrush;
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        if (ratio >= 0.9995)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var startAngle = -Math.PI / 2;
        var endAngle = startAngle + ratio * 2 * Math.PI;
        var start = new Point(
            center.X + radius * Math.Cos(startAngle),
            center.Y + radius * Math.Sin(startAngle));
        var end = new Point(
            center.X + radius * Math.Cos(endAngle),
            center.Y + radius * Math.Sin(endAngle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                0,
                ratio > 0.5,
                SweepDirection.Clockwise,
                true,
                false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }
}