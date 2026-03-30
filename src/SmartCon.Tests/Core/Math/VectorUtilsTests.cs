using SmartCon.Core.Math;
using Xunit;

namespace SmartCon.Tests.Core.Math;

public sealed class VectorUtilsTests
{
    private const double Eps = 1e-6;

    // --- CrossProduct ---

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

    // --- DotProduct ---

    [Fact]
    public void DotProduct_PerpendicularVectors_ReturnsZero()
    {
        var result = VectorUtils.DotProduct(new Vec3(1, 0, 0), new Vec3(0, 1, 0));
        Assert.Equal(0, result, Eps);
    }

    [Fact]
    public void DotProduct_SameDirection_ReturnsPositive()
    {
        var result = VectorUtils.DotProduct(new Vec3(1, 0, 0), new Vec3(3, 0, 0));
        Assert.Equal(3, result, Eps);
    }

    [Fact]
    public void DotProduct_OppositeDirection_ReturnsNegative()
    {
        var result = VectorUtils.DotProduct(new Vec3(1, 0, 0), new Vec3(-2, 0, 0));
        Assert.Equal(-2, result, Eps);
    }

    // --- Length ---

    [Fact]
    public void Length_UnitVector_ReturnsOne()
    {
        Assert.Equal(1.0, VectorUtils.Length(new Vec3(1, 0, 0)), Eps);
    }

    [Fact]
    public void Length_3D_ReturnsCorrect()
    {
        // sqrt(3^2 + 4^2) = 5
        Assert.Equal(5.0, VectorUtils.Length(new Vec3(3, 4, 0)), Eps);
    }

    [Fact]
    public void Length_ZeroVector_ReturnsZero()
    {
        Assert.Equal(0.0, VectorUtils.Length(new Vec3(0, 0, 0)), Eps);
    }

    // --- Normalize ---

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

    // --- AngleBetween ---

    [Fact]
    public void AngleBetween_SameDirection_ReturnsZero()
    {
        var angle = VectorUtils.AngleBetween(new Vec3(1, 0, 0), new Vec3(2, 0, 0));
        Assert.Equal(0, angle, Eps);
    }

    [Fact]
    public void AngleBetween_Perpendicular_ReturnsPiOver2()
    {
        var angle = VectorUtils.AngleBetween(new Vec3(1, 0, 0), new Vec3(0, 1, 0));
        Assert.Equal(System.Math.PI / 2, angle, Eps);
    }

    [Fact]
    public void AngleBetween_Opposite_ReturnsPi()
    {
        var angle = VectorUtils.AngleBetween(new Vec3(1, 0, 0), new Vec3(-1, 0, 0));
        Assert.Equal(System.Math.PI, angle, Eps);
    }

    [Fact]
    public void AngleBetween_45Degrees()
    {
        var angle = VectorUtils.AngleBetween(new Vec3(1, 0, 0), new Vec3(1, 1, 0));
        Assert.Equal(System.Math.PI / 4, angle, Eps);
    }

    // --- AngleBetweenInPlane ---

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

    // --- IsParallel ---

    [Fact]
    public void IsParallel_SameDirection_ReturnsTrue()
    {
        Assert.True(VectorUtils.IsParallel(new Vec3(1, 0, 0), new Vec3(5, 0, 0)));
    }

    [Fact]
    public void IsParallel_OppositeDirection_ReturnsTrue()
    {
        Assert.True(VectorUtils.IsParallel(new Vec3(1, 0, 0), new Vec3(-3, 0, 0)));
    }

    [Fact]
    public void IsParallel_Perpendicular_ReturnsFalse()
    {
        Assert.False(VectorUtils.IsParallel(new Vec3(1, 0, 0), new Vec3(0, 1, 0)));
    }

    // --- IsAntiParallel ---

    [Fact]
    public void IsAntiParallel_OppositeDirection_ReturnsTrue()
    {
        Assert.True(VectorUtils.IsAntiParallel(new Vec3(0, 0, 1), new Vec3(0, 0, -1)));
    }

    [Fact]
    public void IsAntiParallel_SameDirection_ReturnsFalse()
    {
        Assert.False(VectorUtils.IsAntiParallel(new Vec3(1, 0, 0), new Vec3(2, 0, 0)));
    }

    // --- IsCodirectional ---

    [Fact]
    public void IsCodirectional_SameDirection_ReturnsTrue()
    {
        Assert.True(VectorUtils.IsCodirectional(new Vec3(1, 0, 0), new Vec3(5, 0, 0)));
    }

    [Fact]
    public void IsCodirectional_Opposite_ReturnsFalse()
    {
        Assert.False(VectorUtils.IsCodirectional(new Vec3(1, 0, 0), new Vec3(-1, 0, 0)));
    }

    // --- FindPerpendicularAxis ---

    [Fact]
    public void FindPerpendicularAxis_X_ReturnsPerpendicularUnitVector()
    {
        var perp = VectorUtils.FindPerpendicularAxis(new Vec3(1, 0, 0));

        Assert.Equal(1.0, VectorUtils.Length(perp), Eps);
        Assert.Equal(0, VectorUtils.DotProduct(new Vec3(1, 0, 0), perp), Eps);
    }

    [Fact]
    public void FindPerpendicularAxis_Y_ReturnsPerpendicularUnitVector()
    {
        var perp = VectorUtils.FindPerpendicularAxis(new Vec3(0, 1, 0));

        Assert.Equal(1.0, VectorUtils.Length(perp), Eps);
        Assert.Equal(0, VectorUtils.DotProduct(new Vec3(0, 1, 0), perp), Eps);
    }

    [Fact]
    public void FindPerpendicularAxis_Diagonal_ReturnsPerpendicularUnitVector()
    {
        var v = new Vec3(1, 1, 1);
        var perp = VectorUtils.FindPerpendicularAxis(v);

        Assert.Equal(1.0, VectorUtils.Length(perp), Eps);
        Assert.Equal(0, VectorUtils.DotProduct(VectorUtils.Normalize(v), perp), Eps);
    }

    // --- RoundToNearestAngle ---

    [Fact]
    public void RoundToNearestAngle_ExactMultiple_ReturnsSame()
    {
        var angle = 90.0 * System.Math.PI / 180.0;
        var result = VectorUtils.RoundToNearestAngle(angle, 15);
        Assert.Equal(angle, result, Eps);
    }

    [Fact]
    public void RoundToNearestAngle_Between15And30_RoundsToNearest()
    {
        // 20° -> snap to 15°
        var angle = 20.0 * System.Math.PI / 180.0;
        var expected = 15.0 * System.Math.PI / 180.0;
        var result = VectorUtils.RoundToNearestAngle(angle, 15);
        Assert.Equal(expected, result, Eps);
    }

    [Fact]
    public void RoundToNearestAngle_Midpoint_RoundsUp()
    {
        // 22.5° -> rounds to nearest step of 15 -> could be 15 or 30
        // Math.Round uses banker's rounding: 22.5/15 = 1.5 -> rounds to 2 -> 30°
        var angle = 22.5 * System.Math.PI / 180.0;
        var expected = 30.0 * System.Math.PI / 180.0;
        var result = VectorUtils.RoundToNearestAngle(angle, 15);
        Assert.Equal(expected, result, Eps);
    }

    [Fact]
    public void RoundToNearestAngle_NegativeAngle_Works()
    {
        var angle = -10.0 * System.Math.PI / 180.0;
        var expected = -15.0 * System.Math.PI / 180.0;
        var result = VectorUtils.RoundToNearestAngle(angle, 15);
        Assert.Equal(expected, result, Eps);
    }

    // --- DistanceTo ---

    [Fact]
    public void DistanceTo_SamePoint_ReturnsZero()
    {
        var p = new Vec3(1, 2, 3);
        Assert.Equal(0, VectorUtils.DistanceTo(p, p), Eps);
    }

    [Fact]
    public void DistanceTo_KnownDistance()
    {
        Assert.Equal(5.0, VectorUtils.DistanceTo(new Vec3(0, 0, 0), new Vec3(3, 4, 0)), Eps);
    }

    // --- IsAlmostEqual ---

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

    // --- Arithmetic ---

    [Fact]
    public void Add_ReturnsSum()
    {
        var result = VectorUtils.Add(new Vec3(1, 2, 3), new Vec3(4, 5, 6));
        AssertVec3Equal(new Vec3(5, 7, 9), result);
    }

    [Fact]
    public void Subtract_ReturnsDifference()
    {
        var result = VectorUtils.Subtract(new Vec3(5, 7, 9), new Vec3(4, 5, 6));
        AssertVec3Equal(new Vec3(1, 2, 3), result);
    }

    [Fact]
    public void Multiply_ReturnsScaled()
    {
        var result = VectorUtils.Multiply(new Vec3(1, 2, 3), 2);
        AssertVec3Equal(new Vec3(2, 4, 6), result);
    }

    [Fact]
    public void Negate_ReturnsOpposite()
    {
        var result = VectorUtils.Negate(new Vec3(1, -2, 3));
        AssertVec3Equal(new Vec3(-1, 2, -3), result);
    }

    // --- Helpers ---

    private static void AssertVec3Equal(Vec3 expected, Vec3 actual)
    {
        Assert.Equal(expected.X, actual.X, Eps);
        Assert.Equal(expected.Y, actual.Y, Eps);
        Assert.Equal(expected.Z, actual.Z, Eps);
    }
}
