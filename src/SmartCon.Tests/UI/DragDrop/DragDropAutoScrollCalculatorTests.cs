using SmartCon.UI.DragDrop;
using Xunit;

namespace SmartCon.Tests.UI.DragDrop;

public class DragDropAutoScrollCalculatorTests
{
    private const double ViewportHeight = 100.0;
    private const double Tolerance = 20.0;
    private const double MaxSpeed = 100.0;

    [Theory]
    [InlineData(0.0, -10.0)]   // top edge, full factor
    [InlineData(5.0, -7.5)]    // 1/4 from edge, factor 0.75
    [InlineData(10.0, -5.0)]   // middle of top zone, factor 0.5
    [InlineData(15.0, -2.5)]   // 3/4 to boundary, factor 0.25
    [InlineData(20.0, 0.0)]    // boundary, no scroll
    public void ComputeVerticalDelta_TopZone_ReturnsExpected(double cursorY, double expected)
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            cursorY, ViewportHeight, 0.0, Tolerance, MaxSpeed, 0.1);

        Assert.Equal(expected, delta, precision: 5);
    }

    [Theory]
    [InlineData(80.0, 0.0)]    // boundary, no scroll
    [InlineData(85.0, 2.5)]    // 1/4 into bottom zone, factor 0.25
    [InlineData(90.0, 5.0)]    // middle of bottom zone, factor 0.5
    [InlineData(95.0, 7.5)]    // 3/4 to edge, factor 0.75
    [InlineData(100.0, 10.0)]  // bottom edge, full factor
    public void ComputeVerticalDelta_BottomZone_ReturnsExpected(double cursorY, double expected)
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            cursorY, ViewportHeight, 0.0, Tolerance, MaxSpeed, 0.1);

        Assert.Equal(expected, delta, precision: 5);
    }

    [Theory]
    [InlineData(30.0)]
    [InlineData(50.0)]
    [InlineData(70.0)]
    public void ComputeVerticalDelta_MiddleZone_ReturnsZero(double cursorY)
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            cursorY, ViewportHeight, 0.0, Tolerance, MaxSpeed, 0.1);

        Assert.Equal(0.0, delta);
    }

    [Theory]
    [InlineData(0.0, 10.0, -10.0)]   // above top inset, full factor
    [InlineData(5.0, 10.0, -10.0)]   // inside top inset, full factor
    [InlineData(10.0, 10.0, -10.0)]  // at top inset edge, full factor
    [InlineData(15.0, 10.0, -7.5)]   // half way into zone
    [InlineData(30.0, 10.0, 0.0)]    // boundary
    public void ComputeVerticalDelta_WithTopInset_ReturnsExpected(double cursorY, double topInset, double expected)
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            cursorY, ViewportHeight, topInset, Tolerance, MaxSpeed, 0.1);

        Assert.Equal(expected, delta, precision: 5);
    }

    [Fact]
    public void ComputeVerticalDelta_ZeroElapsed_ReturnsZero()
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            0.0, ViewportHeight, 0.0, Tolerance, MaxSpeed, 0.0);

        Assert.Equal(0.0, delta);
    }

    [Fact]
    public void ComputeVerticalDelta_NegativeOrZeroMaxSpeed_ReturnsZero()
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            0.0, ViewportHeight, 0.0, Tolerance, 0.0, 0.1);

        Assert.Equal(0.0, delta);
    }

    [Fact]
    public void ComputeVerticalDelta_ZeroViewportHeight_ReturnsZero()
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            0.0, 0.0, 0.0, Tolerance, MaxSpeed, 0.1);

        Assert.Equal(0.0, delta);
    }

    [Fact]
    public void ComputeVerticalDelta_ZeroTolerance_ReturnsZero()
    {
        var delta = DragDropAutoScrollCalculator.ComputeVerticalDelta(
            0.0, ViewportHeight, 0.0, 0.0, MaxSpeed, 0.1);

        Assert.Equal(0.0, delta);
    }

    [Theory]
    [InlineData(0.0, 0.0, true)]
    [InlineData(15.0, 0.0, true)]
    [InlineData(20.0, 0.0, true)]
    [InlineData(21.0, 0.0, false)]
    [InlineData(79.0, 0.0, false)]
    [InlineData(80.0, 0.0, true)]
    [InlineData(90.0, 0.0, true)]
    [InlineData(100.0, 0.0, true)]
    [InlineData(5.0, 10.0, true)]   // inside top inset
    [InlineData(30.0, 10.0, true)]  // at top inset boundary
    [InlineData(35.0, 10.0, false)] // outside zone
    public void IsInVerticalScrollZone_ReturnsExpected(double cursorY, double topInset, bool expected)
    {
        var result = DragDropAutoScrollCalculator.IsInVerticalScrollZone(
            cursorY, ViewportHeight, topInset, Tolerance);

        Assert.Equal(expected, result);
    }
}
