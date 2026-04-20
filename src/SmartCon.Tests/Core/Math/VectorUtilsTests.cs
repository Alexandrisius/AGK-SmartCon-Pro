using SmartCon.Core.Math;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class VectorUtilsTests
{
    private const double Eps = 1e-6;

    [Fact]
    public void CrossProduct_XcrossY_ReturnsZ()
    {
        var result = VectorUtils.CrossProduct(new Vec3(1, 0, 0), new Vec3(0, 1, 0));
        AssertVec3Equal(new Vec3(0, 0, 1), result);
    }

    [Fact]
    public void CrossProduct_YcrossX_ReturnsNegZ()
    {
        var result = VectorUtils.CrossProduct(new Vec3(0, 1, 0), new Vec3(1, 0, 0));
        AssertVec3Equal(new Vec3(0, 0, -1), result);
    }

    [Fact]
    public void CrossProduct_ParallelVectors_ReturnsZero()
    {
        var result = VectorUtils.CrossProduct(new Vec3(1, 0, 0), new Vec3(2, 0, 0));
        Assert.True(VectorUtils.IsZero(result));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 1, 0, 0)]
    [InlineData(1, 0, 0, 3, 0, 0, 3)]
    [InlineData(1, 0, 0, -2, 0, 0, -2)]
    public void DotProduct_VariousCases(double ax, double ay, double az,
        double bx, double by, double bz, double expected)
    {
        Assert.Equal(expected, VectorUtils.DotProduct(new Vec3(ax, ay, az), new Vec3(bx, by, bz)), Eps);
    }

    [Theory]
    [InlineData(1, 0, 0, 1.0)]
    [InlineData(3, 4, 0, 5.0)]
    [InlineData(0, 0, 0, 0.0)]
    public void Length_VariousCases(double x, double y, double z, double expected)
    {
        Assert.Equal(expected, VectorUtils.Length(new Vec3(x, y, z)), Eps);
    }

    [Fact]
    public void Normalize_NonZero_ReturnsUnitLength()
    {
        var result = VectorUtils.Normalize(new Vec3(3, 4, 0));
        Assert.Equal(1.0, VectorUtils.Length(result), Eps);
    }

    [Fact]
    public void Normalize_ZeroVector_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => VectorUtils.Normalize(new Vec3(0, 0, 0)));
    }

    [Theory]
    [InlineData(1, 0, 0, 2, 0, 0, 0)]
    [InlineData(1, 0, 0, 0, 1, 0, 1.5707963267948966)]
    [InlineData(1, 0, 0, -1, 0, 0, 3.1415926535897931)]
    [InlineData(1, 0, 0, 1, 1, 0, 0.78539816339744828)]
    public void AngleBetween_VariousCases(double ax, double ay, double az,
        double bx, double by, double bz, double expected)
    {
        Assert.Equal(expected, VectorUtils.AngleBetween(new Vec3(ax, ay, az), new Vec3(bx, by, bz)), Eps);
    }

    [Fact]
    public void AngleBetweenInPlane_90DegCCW_ReturnsPositive()
    {
        var angle = VectorUtils.AngleBetweenInPlane(
            new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));
        Assert.Equal(System.Math.PI / 2, angle, Eps);
    }

    [Fact]
    public void AngleBetweenInPlane_90DegCW_ReturnsNegative()
    {
        var angle = VectorUtils.AngleBetweenInPlane(
            new Vec3(0, 1, 0), new Vec3(1, 0, 0), new Vec3(0, 0, 1));
        Assert.Equal(-System.Math.PI / 2, angle, Eps);
    }

    [Theory]
    [InlineData(1, 0, 0, 5, 0, 0, true)]
    [InlineData(1, 0, 0, -3, 0, 0, true)]
    [InlineData(1, 0, 0, 0, 1, 0, false)]
    public void IsParallel_VariousCases(double ax, double ay, double az,
        double bx, double by, double bz, bool expected)
    {
        Assert.Equal(expected, VectorUtils.IsParallel(new Vec3(ax, ay, az), new Vec3(bx, by, bz)));
    }

    [Theory]
    [InlineData(0, 0, 1, 0, 0, -1, true)]
    [InlineData(1, 0, 0, 2, 0, 0, false)]
    public void IsAntiParallel_VariousCases(double ax, double ay, double az,
        double bx, double by, double bz, bool expected)
    {
        Assert.Equal(expected, VectorUtils.IsAntiParallel(new Vec3(ax, ay, az), new Vec3(bx, by, bz)));
    }

    [Theory]
    [InlineData(1, 0, 0, 5, 0, 0, true)]
    [InlineData(1, 0, 0, -1, 0, 0, false)]
    public void IsCodirectional_VariousCases(double ax, double ay, double az,
        double bx, double by, double bz, bool expected)
    {
        Assert.Equal(expected, VectorUtils.IsCodirectional(new Vec3(ax, ay, az), new Vec3(bx, by, bz)));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 1)]
    public void FindPerpendicularAxis_ReturnsPerpendicularUnitVector(double x, double y, double z)
    {
        var v = new Vec3(x, y, z);
        var perp = VectorUtils.FindPerpendicularAxis(v);

        Assert.Equal(1.0, VectorUtils.Length(perp), Eps);
        Assert.Equal(0, VectorUtils.DotProduct(VectorUtils.Normalize(v), perp), Eps);
    }

    [Fact]
    public void RoundToNearestAngle_ExactMultiple_ReturnsSame()
    {
        var angle = 90.0 * System.Math.PI / 180.0;
        var result = VectorUtils.RoundToNearestAngle(angle, 15);
        Assert.Equal(angle, result, Eps);
    }

    [Theory]
    [InlineData(20.0, 15.0)]
    [InlineData(-10.0, -15.0)]
    public void RoundToNearestAngle_NearestStep(double inputDeg, double expectedDeg)
    {
        var angle = inputDeg * System.Math.PI / 180.0;
        var expected = expectedDeg * System.Math.PI / 180.0;
        Assert.Equal(expected, VectorUtils.RoundToNearestAngle(angle, 15), Eps);
    }

    [Fact]
    public void RoundToNearestAngle_Midpoint_RoundsUp()
    {
        var angle = 22.5 * System.Math.PI / 180.0;
        var expected = 30.0 * System.Math.PI / 180.0;
        Assert.Equal(expected, VectorUtils.RoundToNearestAngle(angle, 15), Eps);
    }

    [Theory]
    [InlineData(1, 2, 3, 1, 2, 3, 0)]
    [InlineData(0, 0, 0, 3, 4, 0, 5)]
    public void DistanceTo_VariousCases(double ax, double ay, double az,
        double bx, double by, double bz, double expected)
    {
        Assert.Equal(expected, VectorUtils.DistanceTo(new Vec3(ax, ay, az), new Vec3(bx, by, bz)), Eps);
    }

    [Fact]
    public void IsAlmostEqual_SamePoint_ReturnsTrue()
    {
        var p = new Vec3(1, 2, 3);
        Assert.True(VectorUtils.IsAlmostEqual(p, p));
    }

    [Fact]
    public void IsAlmostEqual_FarPoints_ReturnsFalse()
    {
        Assert.False(VectorUtils.IsAlmostEqual(new Vec3(0, 0, 0), new Vec3(1, 0, 0)));
    }

    [Fact]
    public void Add_ReturnsSum()
    {
        var result = new Vec3(1, 2, 3) + new Vec3(4, 5, 6);
        AssertVec3Equal(new Vec3(5, 7, 9), result);
    }

    [Fact]
    public void Subtract_ReturnsDifference()
    {
        var result = new Vec3(5, 7, 9) - new Vec3(4, 5, 6);
        AssertVec3Equal(new Vec3(1, 2, 3), result);
    }

    [Fact]
    public void Multiply_ReturnsScaled()
    {
        var result = new Vec3(1, 2, 3) * 2;
        AssertVec3Equal(new Vec3(2, 4, 6), result);
    }

    [Fact]
    public void Negate_ReturnsOpposite()
    {
        var result = -new Vec3(1, -2, 3);
        AssertVec3Equal(new Vec3(-1, 2, -3), result);
    }

    private static void AssertVec3Equal(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(expected.X, actual.X, Eps);
        Assert.Equal(expected.Y, actual.Y, Eps);
        Assert.Equal(expected.Z, actual.Z, Eps);
    }
}
