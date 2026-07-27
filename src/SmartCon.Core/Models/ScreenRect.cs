namespace SmartCon.Core.Models;

/// <summary>
/// Axis-aligned rectangle in physical screen pixels (origin top-left, Y down).
/// Used to describe the Revit view window rect (UIView.GetWindowRectangle) and
/// the on-screen bounds of a WPF dialog so the overview zoom can compensate
/// for the dialog occluding the view.
/// </summary>
public sealed record ScreenRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2.0;
    public double CenterY => (Top + Bottom) / 2.0;

    public bool IntersectsWith(ScreenRect other)
        => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}
