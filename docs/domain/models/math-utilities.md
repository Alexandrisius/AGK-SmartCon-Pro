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

## ConnectorOrdering

Детерминированная геометрическая сортировка коннекторов (issue #163):
`ConnectorSet` в Revit API перечисляет коннекторы в случайном порядке, меняющемся
от вызова к вызову — потребители не должны на него полагаться.

Порядок: **X asc** (слева-направо) → **Z desc** (сверху-вниз) → **Y asc** →
tie-break по `Connector.Id` (делает порядок тотальным и воспроизводимым независимо
от порядка входа). Оси сравниваются с толерансом `PositionTolerance` = 1e-6 ft.

Ключ позиции поставляет вызывающий код. Чтобы порядок был стабилен, пока элемент
перемещается/поворачивается (PipeConnectEditor реалайнит элемент на каждом цикле),
ключ должен быть инвариантен к rigid transform — см. `ConnectorService`
(SmartCon.Revit): `FamilyInstance` → семейно-локальные координаты
(`GetTotalTransform().Inverse`), `MEPCurve` → проекция на ось `LocationCurve`.

**Файл:** `Math/ConnectorOrdering.cs`

```csharp
public static class ConnectorOrdering
{
    public const double PositionTolerance = 1e-6;

    public static IReadOnlyList<T> OrderByPosition<T>(
        IEnumerable<T> items,
        Func<T, Vec3> positionSelector,
        Func<T, int> tieBreakerSelector);
}
```

## VectorUtils

Базовые векторные операции с Vec3.

**Файл:** `Math/VectorUtils.cs`

## BestSizeMatcher

Подбор ближайшего доступного типоразмера из LookupTable.

**Файл:** `Math/BestSizeMatcher.cs`

---

## TransitionSizeMatcher

Подбор переходной конфигурации multi-DN семейства (ADR-053): target-порт совпадает
точно с требуемым радиусом, остальные порты меняются минимально (идеально — 0,
«чистый» переход, downstream не трогается). Опции со сменой FamilySymbol и
auto-select исключаются. Pure math над `FamilySizeOption`.

**Файл:** `Math/TransitionSizeMatcher.cs`

```csharp
public static class TransitionSizeMatcher
{
    public static FamilySizeOption? FindBestTransition(
        IReadOnlyList<FamilySizeOption> candidates, double targetRadius,
        int targetConnIdx, IReadOnlyDictionary<int, double> currentRadii,
        double radiusTolerance = 1e-5);

    public static double OtherPortsDelta(
        FamilySizeOption option, int targetConnIdx,
        IReadOnlyDictionary<int, double> currentRadii);
}
```

## SizeRowSymbolMatcher

Сопоставление строки типоразмера с символом DN.

**Файл:** `Math/SizeRowSymbolMatcher.cs`

## LookupTableCsvParser

Парсинг CSV LookupTable семейства Revit.

**Файл:** `Math/LookupTableCsvParser.cs`

---

## PipeLengthAbsorber

Per-level гашение смещения длиной трубы (ADR-052, pure math на Vec3).
Когда подключаемый элемент сам — прямая труба и выравнивание — чистая трансляция,
труба меняет длину вместо жёсткого перемещения: ближний к родителю конец следует
за offset, дальний получает только непоглощённый остаток. Укорочение ограничено
`PipeAbsorption.MinPipeLengthMm` = 100 мм, удлинение без ограничений.

**Файл:** `Math/PipeLengthAbsorber.cs`

```csharp
public static class PipeLengthAbsorber
{
    // null при вырожденной геометрии (нулевой offset или нулевая длина).
    public static PipeAdjustOp? Compute(
        long elementId, Vec3 pipeStart, Vec3 pipeEnd, Vec3 entryPoint,
        Vec3 offset, double minPipeLength);

    // FlexPipe: новый путь точек — двигается только концевая точка со стороны
    // родителя, промежуточные сохраняются verbatim. null если путь < minPathLength.
    public static IReadOnlyList<Vec3>? ComputeFlexPath(
        IReadOnlyList<Vec3> points, Vec3 entryPoint, Vec3 offset, double minPathLength);
}
```
