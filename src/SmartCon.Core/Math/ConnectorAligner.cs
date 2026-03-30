namespace SmartCon.Core.Math;

/// <summary>
/// Алгоритм выравнивания двух коннекторов (чистая математика на Vec3).
/// Вычисляет набор трансформаций (offset + rotations), которые Revit-слой
/// применяет через ElementTransformUtils.MoveElement / RotateElement.
/// 
/// Шаги алгоритма (docs/pipeconnect/algorithms.md):
/// 1. Перемещение: совместить Origin-ы
/// 2. Поворот BasisZ: сделать антипараллельными (коннекторы смотрят друг на друга)
/// 3. Снэп BasisX: округлить угол до ближайшего кратного 15° ("красивый" угол)
/// 4. Коррекция позиции: после поворотов Origin мог сместиться → пересчёт в Revit-слое
/// </summary>
public static class ConnectorAligner
{
    /// <summary>
    /// Шаг снэпа BasisX в градусах. Покрывает 0, 15, 30, 45, 60, 75, 90...
    /// </summary>
    public const double BasisXSnapStepDegrees = 15.0;

    /// <summary>
    /// Вычислить набор трансформаций для выравнивания динамического коннектора
    /// к статическому коннектору.
    /// </summary>
    /// <param name="staticOrigin">Origin статического коннектора</param>
    /// <param name="staticBasisZ">BasisZ статического коннектора (направление)</param>
    /// <param name="staticBasisX">BasisX статического коннектора (ориентация)</param>
    /// <param name="dynamicBasisZ">BasisZ динамического коннектора</param>
    /// <param name="dynamicBasisX">BasisX динамического коннектора</param>
    /// <param name="dynamicOrigin">Origin динамического коннектора</param>
    public static AlignmentResult ComputeAlignment(
        Vec3 staticOrigin, Vec3 staticBasisZ, Vec3 staticBasisX,
        Vec3 dynamicOrigin, Vec3 dynamicBasisZ, Vec3 dynamicBasisX)
    {
        // Шаг 1: вектор перемещения
        var initialOffset = staticOrigin - dynamicOrigin;

        // Шаг 2: поворот BasisZ для антипараллельности
        var basisZRotation = ComputeBasisZRotation(dynamicBasisZ, staticBasisZ);

        // Если был поворот BasisZ, нужно повернуть и dynamicBasisX
        var rotatedDynamicBasisX = basisZRotation is not null
            ? RotateVector(dynamicBasisX, basisZRotation.Axis, basisZRotation.AngleRadians)
            : dynamicBasisX;

        // Шаг 3: снэп BasisX к "красивому" углу
        // planeNormal = -staticBasisZ (антипараллельное направление — нормаль плоскости коннектора)
        var basisXSnap = ComputeBasisXSnap(rotatedDynamicBasisX, staticBasisX, staticBasisZ);

        return new AlignmentResult
        {
            InitialOffset = initialOffset,
            BasisZRotation = basisZRotation,
            BasisXSnap = basisXSnap,
            RotationCenter = staticOrigin
        };
    }

    /// <summary>
    /// Шаг 2: вычисление поворота для антипараллельности BasisZ.
    /// Целевой вектор: targetZ = -static.BasisZ.
    /// </summary>
    internal static RotationStep? ComputeBasisZRotation(Vec3 dynamicBasisZ, Vec3 staticBasisZ)
    {
        var targetZ = -staticBasisZ;
        var angle = VectorUtils.AngleBetween(dynamicBasisZ, targetZ);

        // Уже антипараллельны
        if (angle < VectorUtils.Tolerance)
        {
            return null;
        }

        Vec3 axis;

        // Сонаправлены (angle ≈ PI между dynamic и target, т.е. dynamic ≈ static)
        if (System.Math.Abs(angle - System.Math.PI) < VectorUtils.Tolerance)
        {
            axis = VectorUtils.FindPerpendicularAxis(dynamicBasisZ);
            return new RotationStep(axis, System.Math.PI);
        }

        // Общий случай: ось = cross(dynamic, target), нормализованная
        var cross = VectorUtils.CrossProduct(dynamicBasisZ, targetZ);
        axis = VectorUtils.Normalize(cross);

        return new RotationStep(axis, angle);
    }

    /// <summary>
    /// Шаг 3: снэп BasisX к ближайшему "красивому" углу.
    /// </summary>
    internal static RotationStep? ComputeBasisXSnap(
        Vec3 dynamicBasisX, Vec3 staticBasisX, Vec3 staticBasisZ)
    {
        var currentAngle = VectorUtils.AngleBetweenInPlane(dynamicBasisX, staticBasisX, staticBasisZ);
        var snappedAngle = VectorUtils.RoundToNearestAngle(currentAngle, BasisXSnapStepDegrees);
        var deltaAngle = snappedAngle - currentAngle;

        if (System.Math.Abs(deltaAngle) < VectorUtils.Tolerance)
        {
            return null;
        }

        // Ось поворота = staticBasisZ (нормаль плоскости коннектора)
        return new RotationStep(VectorUtils.Normalize(staticBasisZ), deltaAngle);
    }

    /// <summary>
    /// Поворот вектора вокруг произвольной оси на заданный угол (формула Родрига).
    /// Используется для предсказания положения dynamicBasisX после поворота BasisZ.
    /// </summary>
    internal static Vec3 RotateVector(Vec3 v, Vec3 axis, double angleRadians)
    {
        var k = VectorUtils.Normalize(axis);
        var cosA = System.Math.Cos(angleRadians);
        var sinA = System.Math.Sin(angleRadians);

        // Rodrigues' rotation formula: v' = v*cos(a) + (k×v)*sin(a) + k*(k·v)*(1-cos(a))
        var kCrossV = VectorUtils.CrossProduct(k, v);
        var kDotV = VectorUtils.DotProduct(k, v);

        return v * cosA + kCrossV * sinA + k * (kDotV * (1.0 - cosA));
    }
}
