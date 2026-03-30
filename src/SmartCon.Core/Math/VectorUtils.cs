namespace SmartCon.Core.Math;

/// <summary>
/// Базовые векторные операции для алгоритмов выравнивания (ConnectorAligner).
/// Все вычисления в Internal Units (decimal feet, I-02).
/// Работает с Vec3 (чистая математика, тестируема без Revit runtime).
/// Конвертация XYZ ↔ Vec3 — в SmartCon.Revit/Extensions/Vec3Extensions.cs.
/// </summary>
public static class VectorUtils
{
    /// <summary>
    /// Допуск для сравнения double (≈ 1e-9, стандарт Revit).
    /// </summary>
    public const double Tolerance = 1e-9;

    /// <summary>
    /// Векторное произведение a × b.
    /// </summary>
    public static Vec3 CrossProduct(Vec3 a, Vec3 b)
    {
        return new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
    }

    /// <summary>
    /// Скалярное произведение a · b.
    /// </summary>
    public static double DotProduct(Vec3 a, Vec3 b)
    {
        return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    /// <summary>
    /// Длина вектора.
    /// </summary>
    public static double Length(Vec3 v)
    {
        return System.Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    /// <summary>
    /// Нормализация вектора. Возвращает единичный вектор того же направления.
    /// Бросает ArgumentException если длина ≈ 0.
    /// </summary>
    public static Vec3 Normalize(Vec3 v)
    {
        var len = Length(v);

        if (len < Tolerance)
        {
            throw new ArgumentException("Cannot normalize a zero-length vector.", nameof(v));
        }

        return new Vec3(v.X / len, v.Y / len, v.Z / len);
    }

    /// <summary>
    /// Угол между двумя векторами в радианах [0, PI].
    /// </summary>
    public static double AngleBetween(Vec3 a, Vec3 b)
    {
        var dot = DotProduct(a, b);
        var lenA = Length(a);
        var lenB = Length(b);

        if (lenA < Tolerance || lenB < Tolerance)
        {
            return 0.0;
        }

        var cosAngle = dot / (lenA * lenB);

        // Clamp для защиты от ошибок округления
        cosAngle = System.Math.Clamp(cosAngle, -1.0, 1.0);

        return System.Math.Acos(cosAngle);
    }

    /// <summary>
    /// Угол между двумя векторами в плоскости с заданной нормалью.
    /// Возвращает знаковый угол [-PI, PI].
    /// Используется для вычисления угла поворота BasisX (шаг 3 ConnectorAligner).
    /// </summary>
    public static double AngleBetweenInPlane(Vec3 from, Vec3 to, Vec3 planeNormal)
    {
        var cross = CrossProduct(from, to);
        var dot = DotProduct(from, to);
        var sinComponent = DotProduct(cross, planeNormal);

        return System.Math.Atan2(sinComponent, dot);
    }

    /// <summary>
    /// Проверка параллельности: векторы сонаправлены или антипараллельны.
    /// </summary>
    public static bool IsParallel(Vec3 a, Vec3 b)
    {
        var cross = CrossProduct(a, b);
        return Length(cross) < Tolerance;
    }

    /// <summary>
    /// Проверка антипараллельности: векторы направлены строго противоположно.
    /// </summary>
    public static bool IsAntiParallel(Vec3 a, Vec3 b)
    {
        if (!IsParallel(a, b))
        {
            return false;
        }

        return DotProduct(a, b) < 0;
    }

    /// <summary>
    /// Проверка сонаправленности: векторы направлены в одну сторону.
    /// </summary>
    public static bool IsCodirectional(Vec3 a, Vec3 b)
    {
        if (!IsParallel(a, b))
        {
            return false;
        }

        return DotProduct(a, b) > 0;
    }

    /// <summary>
    /// Находит вектор, перпендикулярный данному.
    /// Используется для разворота на 180° когда BasisZ сонаправлены (шаг 2 ConnectorAligner).
    /// </summary>
    public static Vec3 FindPerpendicularAxis(Vec3 v)
    {
        var normalized = Normalize(v);

        // Выбираем вектор, наименее коллинеарный с v
        var candidate = System.Math.Abs(normalized.X) < 0.9
            ? Vec3.BasisX
            : Vec3.BasisY;

        var perp = CrossProduct(normalized, candidate);
        return Normalize(perp);
    }

    /// <summary>
    /// Округление угла до ближайшего кратного stepDegrees.
    /// Вход/выход в радианах. stepDegrees в градусах (15, 30, 45...).
    /// Используется для снэпа BasisX к «красивому» углу (шаг 3 ConnectorAligner).
    /// </summary>
    public static double RoundToNearestAngle(double angleRadians, double stepDegrees)
    {
        var stepRadians = stepDegrees * System.Math.PI / 180.0;

        if (stepRadians < Tolerance)
        {
            return angleRadians;
        }

        var steps = System.Math.Round(angleRadians / stepRadians);
        return steps * stepRadians;
    }

    /// <summary>
    /// Расстояние между двумя точками.
    /// </summary>
    public static double DistanceTo(Vec3 a, Vec3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Проверка равенства двух точек/векторов с допуском.
    /// </summary>
    public static bool IsAlmostEqual(Vec3 a, Vec3 b, double tolerance = Tolerance)
    {
        return DistanceTo(a, b) < tolerance;
    }

    /// <summary>
    /// Проверка, является ли вектор нулевым (длина &lt; Tolerance).
    /// </summary>
    public static bool IsZero(Vec3 v)
    {
        return Length(v) < Tolerance;
    }

    /// <summary>
    /// Вычитание двух векторов: a - b.
    /// </summary>
    public static Vec3 Subtract(Vec3 a, Vec3 b)
    {
        return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    }

    /// <summary>
    /// Сложение двух векторов: a + b.
    /// </summary>
    public static Vec3 Add(Vec3 a, Vec3 b)
    {
        return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    }

    /// <summary>
    /// Умножение вектора на скаляр.
    /// </summary>
    public static Vec3 Multiply(Vec3 v, double scalar)
    {
        return new Vec3(v.X * scalar, v.Y * scalar, v.Z * scalar);
    }

    /// <summary>
    /// Отрицание вектора: -v.
    /// </summary>
    public static Vec3 Negate(Vec3 v)
    {
        return new Vec3(-v.X, -v.Y, -v.Z);
    }
}
