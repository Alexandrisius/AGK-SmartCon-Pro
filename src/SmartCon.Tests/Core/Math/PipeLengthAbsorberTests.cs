using SmartCon.Core;
using SmartCon.Core.Math;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class PipeLengthAbsorberTests
{
    private const double MinLenFt = PipeAbsorption.MinPipeLengthFt;

    private static double Ft(double mm) => mm / 304.8;

    [Fact]
    public void Compute_ZeroOffset_ReturnsNull()
    {
        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(1, 0, 0), Vec3.Zero, Vec3.Zero, MinLenFt);

        Assert.Null(op);
    }

    [Fact]
    public void Compute_ZeroLengthPipe_ReturnsNull()
    {
        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, Vec3.Zero, Vec3.Zero, new Vec3(Ft(20), 0, 0), MinLenFt);

        Assert.Null(op);
    }

    [Fact]
    public void Compute_CollinearShortenWithinLimit_FullAbsorption()
    {
        var w = new Vec3(Ft(20), 0, 0);

        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(1, 0, 0), Vec3.Zero, w, MinLenFt);

        Assert.NotNull(op);
        Assert.Equal(w, op.StartDelta);
        Assert.Equal(Vec3.Zero, op.EndDelta);
        Assert.Equal(Ft(20), op.AbsorbedLengthFt, 9);
    }

    [Fact]
    public void Compute_ShortenCappedAtMinimum_RemainderGoesToFarEnd()
    {
        // Pipe 250 mm, offset 200 mm → only 150 mm absorbed (min 100 mm), 50 mm remainder.
        var w = new Vec3(Ft(200), 0, 0);

        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(Ft(250), 0, 0), Vec3.Zero, w, MinLenFt);

        Assert.NotNull(op);
        Assert.Equal(Ft(150), op.AbsorbedLengthFt, 9);
        Assert.Equal(w, op.StartDelta);
        Assert.Equal(new Vec3(Ft(50), 0, 0), op.EndDelta);
    }

    [Fact]
    public void Compute_PipeShorterThanMinimum_NoShortening()
    {
        // Pipe 80 mm (< 100 mm minimum) cannot shorten at all → pure translation.
        var w = new Vec3(Ft(20), 0, 0);

        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(Ft(80), 0, 0), Vec3.Zero, w, MinLenFt);

        Assert.NotNull(op);
        Assert.Equal(0.0, op.AbsorbedLengthFt);
        Assert.Equal(w, op.StartDelta);
        Assert.Equal(w, op.EndDelta);
    }

    [Fact]
    public void Compute_OffsetAwayFromAxis_LengthensWithoutLimit()
    {
        var w = new Vec3(Ft(-500), 0, 0);

        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(Ft(120), 0, 0), Vec3.Zero, w, MinLenFt);

        Assert.NotNull(op);
        Assert.Equal(Ft(-500), op.AbsorbedLengthFt, 9);
        Assert.Equal(w, op.StartDelta);
        Assert.Equal(Vec3.Zero, op.EndDelta);
    }

    [Fact]
    public void Compute_PerpendicularOffset_PureTranslation()
    {
        var w = new Vec3(Ft(20), 0, 0);

        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(0, 1, 0), Vec3.Zero, w, MinLenFt);

        Assert.NotNull(op);
        Assert.Equal(0.0, op.AbsorbedLengthFt, 9);
        Assert.Equal(w, op.StartDelta);
        Assert.Equal(w, op.EndDelta);
    }

    [Fact]
    public void Compute_EntryAtEndpoint1_DeltasMappedToCorrectEndpoints()
    {
        // Near end is endpoint 1 → EndDelta carries the offset.
        var w = new Vec3(Ft(20), 0, 0);

        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(1, 0, 0), new Vec3(1, 0, 0), w, MinLenFt);

        Assert.NotNull(op);
        Assert.Equal(Vec3.Zero, op.StartDelta);
        Assert.Equal(w, op.EndDelta);
        // Moving endpoint 1 in +X lengthens the pipe.
        Assert.Equal(Ft(-20), op.AbsorbedLengthFt, 9);
    }

    [Fact]
    public void Compute_SlightlyOffAxis_PerpendicularRemainderStaysAtFarEnd()
    {
        // Offset mostly along X with a small Y component: axial part absorbed,
        // perpendicular remainder passes through to the far end.
        var w = new Vec3(Ft(20), Ft(1), 0);

        var op = PipeLengthAbsorber.Compute(
            1, Vec3.Zero, new Vec3(1, 0, 0), Vec3.Zero, w, MinLenFt);

        Assert.NotNull(op);
        Assert.Equal(Ft(20), op.AbsorbedLengthFt, 6);
        Assert.Equal(w, op.StartDelta);
        var remainder = op.EndDelta;
        Assert.Equal(0.0, remainder.X, 6);
        Assert.Equal(Ft(1), remainder.Y, 6);
    }

    [Fact]
    public void ComputeFlexPath_EntryAtStart_MovesOnlyFirstPoint()
    {
        var w = new Vec3(Ft(20), Ft(5), 0);
        var points = new List<Vec3>
        {
            new(0, 0, 0), new(Ft(100), Ft(30), 0), new(Ft(200), 0, 0),
        };

        var result = PipeLengthAbsorber.ComputeFlexPath(points, Vec3.Zero, w, MinLenFt);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal(w, result[0]);
        // Промежуточная и концевая точки не тронуты.
        Assert.Equal(new Vec3(Ft(100), Ft(30), 0), result[1]);
        Assert.Equal(new Vec3(Ft(200), 0, 0), result[2]);
    }

    [Fact]
    public void ComputeFlexPath_EntryAtEnd_MovesOnlyLastPoint()
    {
        var w = new Vec3(0, 0, Ft(50));
        var points = new List<Vec3>
        {
            new(0, 0, 0), new(Ft(100), Ft(30), 0), new(Ft(200), 0, 0),
        };

        var result = PipeLengthAbsorber.ComputeFlexPath(points, new Vec3(Ft(200), 0, 0), w, MinLenFt);

        Assert.NotNull(result);
        Assert.Equal(new Vec3(0, 0, 0), result[0]);
        Assert.Equal(new Vec3(Ft(100), Ft(30), 0), result[1]);
        Assert.Equal(new Vec3(Ft(200), 0, Ft(50)), result[2]);
    }

    [Fact]
    public void ComputeFlexPath_PathTooShortAfterMove_ReturnsNull()
    {
        // Two-point path of 90 mm; moving one endpoint onto the other collapses it below 100 mm.
        var points = new List<Vec3> { new(0, 0, 0), new(Ft(90), 0, 0) };

        var result = PipeLengthAbsorber.ComputeFlexPath(
            points, Vec3.Zero, new Vec3(Ft(90), 0, 0), MinLenFt);

        Assert.Null(result);
    }

    [Fact]
    public void ComputeFlexPath_ZeroOffset_ReturnsNull()
    {
        var points = new List<Vec3> { new(0, 0, 0), new(Ft(200), 0, 0) };

        Assert.Null(PipeLengthAbsorber.ComputeFlexPath(points, Vec3.Zero, Vec3.Zero, MinLenFt));
    }

    [Fact]
    public void ComputeFlexPath_SinglePoint_ReturnsNull()
    {
        var points = new List<Vec3> { new(0, 0, 0) };

        Assert.Null(PipeLengthAbsorber.ComputeFlexPath(
            points, Vec3.Zero, new Vec3(Ft(20), 0, 0), MinLenFt));
    }
}
