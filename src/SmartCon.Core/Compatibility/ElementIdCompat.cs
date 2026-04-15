using Autodesk.Revit.DB;

namespace SmartCon.Core.Compatibility;

/// <summary>
/// Абстракция над ElementId для совместимости Revit 2021-2023 (32-bit) и 2024+ (64-bit).
/// Revit 2021-2023: ElementId(int), .IntegerValue
/// Revit 2024+:     ElementId(long), .Value
/// </summary>
public static class ElementIdCompat
{
#if REVIT2024_OR_GREATER
    /// <summary>Получить числовое значение ElementId (64-bit для 2024+).</summary>
    public static long GetValue(this ElementId id) => id.Value;

    /// <summary>Создать ElementId из числового значения.</summary>
    public static ElementId Create(long value) => new(value);

    /// <summary>Получить хэш-код ElementId.</summary>
    public static int GetStableHashCode(this ElementId id) => id.Value.GetHashCode();
#else
    /// <summary>Получить числовое значение ElementId (32-bit для 2021-2023).</summary>
    public static long GetValue(this ElementId id) => id.IntegerValue;

    /// <summary>Создать ElementId из числового значения.</summary>
    /// <remarks>Revit 2021-2023: ElementId — 32-bit. Значения > int.MaxValue недопустимы.</remarks>
    public static ElementId Create(long value)
    {
        if (value is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException(
                $"ElementId value {value} was serialized under Revit 2024+ (64-bit) " +
                $"and cannot be restored in Revit 2021-2023 (32-bit). Data migration required.");
        return new((int)value);
    }

    /// <summary>Получить хэш-код ElementId.</summary>
    public static int GetStableHashCode(this ElementId id) => id.IntegerValue.GetHashCode();
#endif
}
