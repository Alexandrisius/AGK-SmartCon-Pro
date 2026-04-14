using SmartCon.Core.Math;
using Xunit;

namespace SmartCon.Tests.Core.Math;

/// <summary>
/// Unit-тесты ConnectorAligner. Без Revit runtime — только Vec3/VectorUtils.
/// </summary>
public sealed class ConnectorAlignerTests
{
    private const double Eps = 1e-9;

    // ─────────────────────────────────────────────────────────────────────────
    // ComputeBasisZRotation
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BasisZRotation_AlreadyAntiParallel_ReturnsNull()
    {
        // dynamic.Z уже антипараллелен static.Z (т.е. dynamic.Z = -static.Z)
        var staticZ = Vec3.BasisZ;
        var dynamicZ = -Vec3.BasisZ;

        var result = ConnectorAligner.ComputeBasisZRotation(dynamicZ, staticZ);

        Assert.Null(result);
    }

    [Fact]
    public void BasisZRotation_CodirectionalVectors_Returns180DegreeRotation()
    {
        // dynamic.Z сонаправлен static.Z → нужен разворот 180°
        var staticZ = Vec3.BasisZ;
        var dynamicZ = Vec3.BasisZ;   // сонаправлены → targetZ = -Z, angle(Z, -Z) = PI

        var result = ConnectorAligner.ComputeBasisZRotation(dynamicZ, staticZ);

        Assert.NotNull(result);
        Assert.Equal(System.Math.PI, result!.AngleRadians, 6);
        // Ось должна быть перпендикулярна BasisZ
        var dot = VectorUtils.DotProduct(result.Axis, Vec3.BasisZ);
        Assert.True(System.Math.Abs(dot) < 1e-6, "Ось поворота должна быть перп. Z");
    }

    [Fact]
    public void BasisZRotation_GeneralCase_RotatesCorrectly()
    {
        // dynamic.Z смотрит вдоль +X, static.Z смотрит вдоль +Z
        // targetZ = -static.Z = -Z
        // Цель: повернуть +X → -Z
        var staticZ = Vec3.BasisZ;
        var dynamicZ = Vec3.BasisX;

        var result = ConnectorAligner.ComputeBasisZRotation(dynamicZ, staticZ);

        Assert.NotNull(result);
        // После поворота dynamicZ должен совпасть с -staticZ
        var rotated = ConnectorAligner.RotateVector(dynamicZ, result!.Axis, result.AngleRadians);
        var targetZ = -staticZ;
        Assert.True(VectorUtils.IsAlmostEqual(rotated, targetZ, 1e-9),
            $"Повёрнутый вектор {rotated} должен быть {targetZ}");
    }

    [Fact]
    public void BasisZRotation_OppositeVectors_ReturnsNullOrZeroAngle()
    {
        // dynamic.Z = -static.Z → уже антипараллельны → null
        var staticZ = new Vec3(0, 1, 0);
        var dynamicZ = new Vec3(0, -1, 0);

        var result = ConnectorAligner.ComputeBasisZRotation(dynamicZ, staticZ);

        Assert.Null(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ComputeBasisXSnap
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BasisXSnap_AlreadyAligned_ReturnsNull()
    {
        // static.X и dynamic.X сонаправлены → угол = 0 → округлённый = 0 → delta = 0
        var result = ConnectorAligner.ComputeBasisXSnap(Vec3.BasisX, Vec3.BasisX, Vec3.BasisZ);
        Assert.Null(result);
    }

    [Fact]
    public void BasisXSnap_7Degrees_SnapsToZero()
    {
        // 7° < 7.5° (половина шага 15°) → снэп к 0°
        var angle7 = 7.0 * System.Math.PI / 180.0;
        var dynamicX = new Vec3(System.Math.Cos(angle7), System.Math.Sin(angle7), 0);

        var result = ConnectorAligner.ComputeBasisXSnap(dynamicX, Vec3.BasisX, Vec3.BasisZ);

        Assert.NotNull(result);
        // После поворота delta, итоговый угол должен быть ≈ 0
        var snappedAngle = VectorUtils.RoundToNearestAngle(
            VectorUtils.AngleBetweenInPlane(dynamicX, Vec3.BasisX, Vec3.BasisZ),
            ConnectorAligner.BasisXSnapStepDegrees);
        Assert.Equal(0.0, snappedAngle, 6);
    }

    [Fact]
    public void BasisXSnap_8Degrees_SnapsTo15()
    {
        // dynamicX повёрнут на -8° (CW) от staticX → AngleBetweenInPlane = +8°
        // +8° > 7.5° (пол-шага 15°) → снэп к +15°
        var angle8 = -8.0 * System.Math.PI / 180.0;
        var dynamicX = new Vec3(System.Math.Cos(angle8), System.Math.Sin(angle8), 0);

        var currentAngle = VectorUtils.AngleBetweenInPlane(dynamicX, Vec3.BasisX, Vec3.BasisZ);
        var snappedAngle = VectorUtils.RoundToNearestAngle(currentAngle, ConnectorAligner.BasisXSnapStepDegrees);

        Assert.Equal(15.0 * System.Math.PI / 180.0, snappedAngle, 6);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RotateVector (формула Родрига)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RotateVector_90DegAroundZ_RotatesXToY()
    {
        var rotated = ConnectorAligner.RotateVector(Vec3.BasisX, Vec3.BasisZ, System.Math.PI / 2);
        Assert.True(VectorUtils.IsAlmostEqual(rotated, Vec3.BasisY, 1e-9),
            $"Ожидалось {Vec3.BasisY}, получено {rotated}");
    }

    [Fact]
    public void RotateVector_180DegAroundY_NegatesX()
    {
        var rotated = ConnectorAligner.RotateVector(Vec3.BasisX, Vec3.BasisY, System.Math.PI);
        var expected = new Vec3(-1, 0, 0);
        Assert.True(VectorUtils.IsAlmostEqual(rotated, expected, 1e-9),
            $"Ожидалось {expected}, получено {rotated}");
    }

    [Fact]
    public void RotateVector_ZeroAngle_ReturnsSameVector()
    {
        var v = new Vec3(1, 2, 3);
        var rotated = ConnectorAligner.RotateVector(v, Vec3.BasisZ, 0.0);
        Assert.True(VectorUtils.IsAlmostEqual(rotated, v, 1e-9));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ComputeAlignment — полный алгоритм
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeAlignment_SamePosition_ZeroOffset()
    {
        var origin = new Vec3(1, 2, 3);
        var result = ConnectorAligner.ComputeAlignment(
            staticOrigin: origin, staticBasisZ: Vec3.BasisZ, staticBasisX: Vec3.BasisX,
            dynamicOrigin: origin, dynamicBasisZ: -Vec3.BasisZ, dynamicBasisX: Vec3.BasisX);

        Assert.True(VectorUtils.IsZero(result.InitialOffset), "Origins совпадают → offset = 0");
        Assert.Null(result.BasisZRotation); // уже антипараллельны
    }

    [Fact]
    public void ComputeAlignment_OffsetOnly_CorrectInitialOffset()
    {
        // Static коннектор в (5,0,0), dynamic в (0,0,0)
        // BasisZ уже антипараллельны
        var staticOrigin = new Vec3(5, 0, 0);
        var dynamicOrigin = Vec3.Zero;

        var result = ConnectorAligner.ComputeAlignment(
            staticOrigin, Vec3.BasisZ, Vec3.BasisX,
            dynamicOrigin, -Vec3.BasisZ, Vec3.BasisX);

        var expectedOffset = staticOrigin - dynamicOrigin;
        Assert.True(VectorUtils.IsAlmostEqual(result.InitialOffset, expectedOffset));
        Assert.Equal(staticOrigin, result.RotationCenter);
    }

    [Fact]
    public void ComputeAlignment_CodirectionalZ_GeneratesRotation()
    {
        // Dynamic BasisZ сонаправлен статическому → нужен разворот
        var result = ConnectorAligner.ComputeAlignment(
            Vec3.Zero, Vec3.BasisZ, Vec3.BasisX,
            Vec3.Zero, Vec3.BasisZ, Vec3.BasisX);  // оба смотрят в одну сторону

        Assert.NotNull(result.BasisZRotation);
        Assert.Equal(System.Math.PI, result.BasisZRotation!.AngleRadians, 6);
    }

    [Fact]
    public void ComputeAlignment_RotationCenter_IsStaticOrigin()
    {
        var staticOrigin = new Vec3(10, 20, 30);
        var result = ConnectorAligner.ComputeAlignment(
            staticOrigin, Vec3.BasisZ, Vec3.BasisX,
            Vec3.Zero, -Vec3.BasisZ, Vec3.BasisX);

        Assert.Equal(staticOrigin, result.RotationCenter);
    }

    [Fact]
    public void ComputeAlignment_GeneralCase_BasisZBecomesAntiParallel()
    {
        // Dynamic BasisZ смотрит вдоль +Y, Static BasisZ смотрит вдоль +Z
        // После поворота dynamic.BasisZ должен стать -Z
        var staticBasisZ = Vec3.BasisZ;
        var dynamicBasisZ = Vec3.BasisY;

        var result = ConnectorAligner.ComputeAlignment(
            Vec3.Zero, staticBasisZ, Vec3.BasisX,
            Vec3.Zero, dynamicBasisZ, Vec3.BasisX);

        Assert.NotNull(result.BasisZRotation);

        var rotated = ConnectorAligner.RotateVector(
            dynamicBasisZ,
            result.BasisZRotation!.Axis,
            result.BasisZRotation.AngleRadians);

        var expectedTargetZ = -staticBasisZ;
        Assert.True(VectorUtils.IsAlmostEqual(rotated, expectedTargetZ, 1e-9),
            $"После поворота BasisZ должен быть {expectedTargetZ}, получено {rotated}");
    }
}
