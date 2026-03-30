using Autodesk.Revit.DB;
using SmartCon.Core.Math;

namespace SmartCon.Revit.Extensions;

/// <summary>
/// Extension-методы для Revit XYZ.
/// Включает конвертацию XYZ ↔ Vec3 и утилиты для Revit-слоя.
/// </summary>
public static class XYZExtensions
{
    /// <summary>
    /// Конвертация Revit XYZ → Core Vec3.
    /// </summary>
    public static Vec3 ToVec3(this XYZ xyz) => new(xyz.X, xyz.Y, xyz.Z);

    /// <summary>
    /// Конвертация Core Vec3 → Revit XYZ.
    /// </summary>
    public static XYZ ToXYZ(this Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Создать Line из точки и направления (для оси вращения).
    /// Используется в RotateElement.
    /// </summary>
    public static Line ToAxisLine(this XYZ origin, XYZ direction)
    {
        return Line.CreateBound(origin, origin + direction);
    }

    /// <summary>
    /// Проверка приблизительного равенства двух точек.
    /// </summary>
    public static bool IsAlmostEqualTo(this XYZ a, XYZ b, double tolerance = 1e-9)
    {
        return a.DistanceTo(b) < tolerance;
    }
}
