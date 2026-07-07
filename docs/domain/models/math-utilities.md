---
module: math-utilities
---
# Math Utilities

> Загружать: при работе с векторной математикой Core.
> Источник истины: `src/SmartCon.Core/Math/*.cs`.

Вспомогательные математические классы в `SmartCon.Core/Math/`. Используют `Vec3` (ADR-009) вместо `XYZ` для тестируемости без Revit runtime.

## Vec3

Лёгкий иммутабельный 3D-вектор для чистой математики в Core (ADR-009).
Используется в `VectorUtils` и `ConnectorAligner` вместо `XYZ`, чтобы Core оставался тестируемым без Revit runtime.

**Файл:** `SmartCon.Core/Math/Vec3.cs`

```csharp
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 BasisX = new(1, 0, 0);
    public static readonly Vec3 BasisY = new(0, 1, 0);
    public static readonly Vec3 BasisZ = new(0, 0, 1);

    // Операторы: +, -, unary -, * (scalar), * (commutative scalar)
}
```

---

## AlignmentResult

Результат вычисления `ConnectorAligner.ComputeAlignment()`.
Содержит набор трансформаций для применения через `ITransformService`.

**Файл:** `SmartCon.Core/Math/AlignmentResult.cs`

```csharp
public sealed class AlignmentResult
{
    public required Vec3 InitialOffset { get; init; }    // Шаг 1: смещение
    public RotationStep? BasisZRotation { get; init; }   // Шаг 2: поворот BasisZ (null если уже антипараллельны)
    public RotationStep? BasisXSnap { get; init; }       // Шаг 3: снэп BasisX к 15° (null если delta ≈ 0)
    public required Vec3 RotationCenter { get; init; }  // = static.Origin
}

public sealed record RotationStep(Vec3 Axis, double AngleRadians);
```

---

## ConnectorAligner

Вычисление матриц поворота и смещения для выравнивания коннекторов (ADR-009, Vec3).

**Файл:** `Math/ConnectorAligner.cs`

## VectorUtils

Базовые векторные операции с Vec3.

**Файл:** `Math/VectorUtils.cs`

## BestSizeMatcher

Подбор ближайшего доступного типоразмера из LookupTable.

**Файл:** `Math/BestSizeMatcher.cs`

## SizeRowSymbolMatcher

Сопоставление строки типоразмера с символом DN.

**Файл:** `Math/SizeRowSymbolMatcher.cs`

## LookupTableCsvParser

Парсинг CSV LookupTable семейства Revit.

**Файл:** `Math/LookupTableCsvParser.cs`
