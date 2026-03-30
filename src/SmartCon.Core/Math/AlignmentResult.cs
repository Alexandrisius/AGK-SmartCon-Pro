namespace SmartCon.Core.Math;

/// <summary>
/// Результат вычисления выравнивания ConnectorAligner.
/// Содержит набор операций для применения в Revit через ITransformService.
/// Все координаты в Internal Units (decimal feet, I-02).
/// </summary>
public sealed class AlignmentResult
{
    /// <summary>
    /// Шаг 1: вектор начального смещения (static.Origin - dynamic.Origin).
    /// </summary>
    public required Vec3 InitialOffset { get; init; }

    /// <summary>
    /// Шаг 2: поворот BasisZ для антипараллельности.
    /// null если BasisZ уже антипараллельны.
    /// </summary>
    public RotationStep? BasisZRotation { get; init; }

    /// <summary>
    /// Шаг 3: снэп BasisX к ближайшему "красивому" углу.
    /// null если deltaAngle ≈ 0.
    /// </summary>
    public RotationStep? BasisXSnap { get; init; }

    /// <summary>
    /// Точка, относительно которой производятся повороты (static.Origin).
    /// </summary>
    public required Vec3 RotationCenter { get; init; }
}

/// <summary>
/// Одна операция поворота: ось + угол.
/// </summary>
public sealed record RotationStep(Vec3 Axis, double AngleRadians);
