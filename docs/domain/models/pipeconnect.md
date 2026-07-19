---
module: pipeconnect
---
# Модели PipeConnect

> Загружать: при работе с моделями данных PipeConnect.
> Источник истины: `src/SmartCon.Core/Models/*.cs`.

## ConnectorProxy

Иммутабельный снапшот состояния коннектора на момент выбора. Не хранить между транзакциями.

**Файл:** `SmartCon.Core/Models/ConnectorProxy.cs`

```csharp
public sealed record ConnectorProxy
{
    public ElementId OwnerElementId { get; init; }
    public int ConnectorIndex { get; init; }
    public XYZ Origin { get; init; }                  // Internal Units (decimal feet)
    public XYZ BasisZ { get; init; }                  // нормаль плоскости коннектора
    public XYZ BasisX { get; init; }                  // для вычисления угла поворота
    public double Radius { get; init; }               // Internal Units
    public Domain Domain { get; init; }                // DomainPiping, DomainHvac, etc.
    public ConnectionTypeCode ConnectionTypeCode { get; init; }
    public string ConnectionName { get; init; } = "";        // issue #64: имя из "code.name.description"
    public string ConnectionDescription { get; init; } = ""; // issue #64: описание (с точками)
    public bool IsFree { get; init; }                 // AllRefs.IsEmpty == true
}
```

---

## ConnectionTypeCode

Строго типизированная обёртка над **числовым кодом** из поля `Connector.Description`.
Парсит только первый сегмент до `.` (для полного разбора см. `ConnectorDescription`).

**Файл:** `SmartCon.Core/Models/ConnectionTypeCode.cs`

```csharp
public readonly record struct ConnectionTypeCode(int Value)
{
    public static readonly ConnectionTypeCode Undefined = new(0);
    public bool IsDefined => Value != 0;
    public override string ToString() => Value.ToString();

    // Парсит "1.Резьба.ГОСТ 6357" → Code=1 (только число).
    // Для полного разбора code+name+description используйте ConnectorDescription.Parse.
    public static ConnectionTypeCode Parse(string? raw) =>
        int.TryParse(raw?.Split('.')[0].Trim(), out var v) && v != 0 ? new(v) : Undefined;
}
```

> **Используется в 40+ hot-path местах** (CtcGuesser, FittingMapper, FittingInsertService,
> CtcGuessService, CtcFamilyWriter, FittingCardBuilder, ConnectExecutor, тесты) — везде, где
> достаточно **только числового кода** для сравнения.

---

## ConnectorDescription

Полная распарсенная тройка `code.name.description` из строки описания коннектора.
Используется для верификации против пользовательского маппинга (issue #64).

**Файл:** `SmartCon.Core/Models/ConnectorDescription.cs`

```csharp
public sealed record ConnectorDescription(
    ConnectionTypeCode Code,
    string Name,
    string Description)
{
    public static readonly ConnectorDescription Undefined = new(ConnectionTypeCode.Undefined, "", "");

    public bool IsDefined => Code.IsDefined;

    // Парсит "1.Резьба наружная.ГОСТ 6357-81 §4.2.5" → (Code=1, Name="Резьба наружная", Description="ГОСТ 6357-81 §4.2.5")
    // Третий сегмент может содержать точки (ГОСТы, разделы) — Split с count=3 сохраняет остаток.
    public static ConnectorDescription Parse(string? raw);

    // Строит описание из ConnectorProxy (для повторного парсинга после обновления через MiniTypeSelector).
    public static ConnectorDescription FromProxy(ConnectorProxy proxy);

    // Сравнение с ConnectorTypeDefinition из маппинга:
    // Trim + OrdinalIgnoreCase для всех 3 полей, пустое != непустое (строго).
    public bool Matches(ConnectorTypeDefinition definition);

    // Полная проверка: парсинг proxy и поиск хотя бы одного совпадения в mapping.
    // Issue #64: если code=1 совпадает, но name/description отличаются → false → MiniTypeSelector вызывается.
    public static bool IsKnownTypeDefinition(
        ConnectorDescription parsed,
        IReadOnlyList<ConnectorTypeDefinition> mapping);
}
```

> **Почему отдельный record от `ConnectionTypeCode`?** `ConnectionTypeCode` — 4-байтовый
> value type, используемый в 40+ hot-path сравнениях, где достаточно числа. Этот record
> несёт полную тройку и используется только в `PipeConnectSessionBuilder.IsKnownTypeDefinition`.

---

## ConnectorTypeDefinition

Определение типа коннектора для пользовательского справочника. Хранится per-project
в `ExtensibleStorage.DataStorage` активного `Document` ([ADR-012](../../adr/012-per-project-extensible-storage.md)).

**Файл:** `SmartCon.Core/Models/ConnectorTypeDefinition.cs`

```csharp
public sealed record ConnectorTypeDefinition
{
    public int Code { get; init; }
    public string Name { get; init; } = string.Empty;        // "Сварка", "Резьба"
    public string Description { get; init; } = string.Empty;  // подробное описание
}
```

Записывается в `ALL_MODEL_DESCRIPTION` типоразмера трубы или `Connector.Description` фитинга
в формате `"{Code}.{Name}.{Description}"` через `RevitFamilyConnectorService.SetConnectorTypeCode`.
Тот же формат читает `ConnectorDescription.Parse`.

---

## FittingMapping

Модель одного семейства фитинга в правиле маппинга.

**Файл:** `SmartCon.Core/Models/FittingMapping.cs`

```csharp
public sealed record FittingMapping
{
    public string FamilyName { get; init; } = string.Empty;
    public string SymbolName { get; init; } = "*";   // "*" = любой типоразмер
    public int Priority { get; init; }                // меньше = предпочтительнее
}
```

---

## FittingMappingRule

Расширенное правило маппинга: пара типов коннекторов -> список семейств.

**Файл:** `SmartCon.Core/Models/FittingMappingRule.cs`

```csharp
public sealed record FittingMappingRule
{
    public ConnectionTypeCode FromType { get; init; }
    public ConnectionTypeCode ToType { get; init; }
    public bool IsDirectConnect { get; init; }        // true = совместимы без фитинга
    public List<FittingMapping> FittingFamilies { get; init; } = [];
}
```

---

## ConnectionGraph

Направленный граф соединённых MEP-элементов. Строится перед трансформацией. Неизменяем после создания.

**Файл:** `SmartCon.Core/Models/ConnectionGraph.cs`

```csharp
public sealed class ConnectionGraph
{
    public IReadOnlyList<ElementId> Nodes { get; }
    public IReadOnlyList<ConnectionEdge> Edges { get; }
    public ElementId RootId { get; }

    public IEnumerable<ElementId> GetChainFrom(ElementId startId) { ... }
}
```

---

## ConnectionEdge

Ребро графа — пара коннекторов между двумя элементами.

**Файл:** `SmartCon.Core/Models/ConnectionEdge.cs`

```csharp
public sealed record ConnectionEdge(
    ElementId FromElementId,
    int FromConnectorIndex,
    ElementId ToElementId,
    int ToConnectorIndex
);
```

---

## PipeConnectionSession

Мутабельный контекст одной сессии соединения. Живёт на уровне ViewModel, сбрасывается при отмене.

**Файл:** `SmartCon.Core/Models/PipeConnectionSession.cs`

```csharp
public sealed class PipeConnectionSession
{
    public ConnectorProxy? StaticConnector { get; set; }
    public ConnectorProxy? DynamicConnector { get; set; }
    public ConnectionGraph? DynamicChain { get; set; }
    public List<FittingMappingRule> ProposedFittings { get; set; } = [];
    public double RotationAngleDeg { get; set; } = 0;
    public bool MoveEntireChain { get; set; } = false;
    public PipeConnectState State { get; set; } = PipeConnectState.AwaitingStaticSelection;

    // Phase 4 — результат подбора параметров (S4)
    public bool   NeedsAdapter          { get; set; } = false; // радиус не совпал точно
    public double OriginalDynamicRadius { get; set; } = 0.0;   // до изменения
    public double ActualDynamicRadius   { get; set; } = 0.0;   // после подбора
}
```

---

## PipeConnectState

Enum состояний state machine.

**Файл:** `SmartCon.Core/Models/PipeConnectState.cs`

```csharp
public enum PipeConnectState
{
    AwaitingStaticSelection,
    AwaitingDynamicSelection,
    AligningConnectors,
    ResolvingParameters,
    ResolvingFittings,
    PostProcessing,
    Committed,
    Cancelled
}
```

---

## ParameterDependency

Описание зависимости параметра коннектора от параметра семейства.

**Файл:** `SmartCon.Core/Models/ParameterDependency.cs`

```csharp
public sealed record ParameterDependency(
    BuiltInParameter? BuiltIn,
    string? SharedParamName,
    string? Formula,              // null если прямой параметр (без формулы)
    bool IsInstance,              // false = параметр типа
    string? DirectParamName = null, // имя параметра семейства, прямо управляющего Radius
    string? RootParamName   = null  // корневой параметр (если Formula это цепочка формул)
);
```

---

## PipeConnectSessionContext

Immutable контекст сессии, создаваемый PipeConnectSessionBuilder и передаваемый в ViewModel.

**Файл:** `SmartCon.Core/Models/PipeConnectSessionContext.cs`

```csharp
public sealed class PipeConnectSessionContext
{
    public required ConnectorProxy StaticConnector { get; init; }
    public required ConnectorProxy DynamicConnector { get; init; }
    public required AlignmentResult AlignResult { get; init; }
    public double? ParamTargetRadius { get; init; }
    public bool ParamExpectNeedsAdapter { get; init; }
    public required List<FittingMappingRule> ProposedFittings { get; init; }
    public ConnectionGraph? ChainGraph { get; init; }
    public required VirtualCtcStore VirtualCtcStore { get; init; }
    public IReadOnlyList<LookupColumnConstraint> LookupConstraints { get; init; } = [];
}
```

---

## VirtualCtcStore

Виртуальное хранилище CTC overrides — хранит назначенные типы коннекторов до записи в семейство.

**Файл:** `SmartCon.Core/Models/VirtualCtcStore.cs`

```csharp
public sealed class VirtualCtcStore
{
    public bool HasPendingWrites { get; }
    public void Set(ElementId elementId, int connectorIndex, ConnectionTypeCode ctc, ConnectorTypeDefinition? definition);
    public void RemoveForElement(ElementId elementId);
    public ConnectionTypeCode? Get(ElementId elementId, int connectorIndex);
    public IReadOnlyDictionary<int, ConnectionTypeCode> GetOverridesForElement(ElementId elementId);
}
```

---

## NetworkSnapshot

Снапшот состояния элемента для отката цепочки (кнопка `−`). Для FamilyInstance —
полный Transform, для MEPCurve — концы кривой, для FlexPipe — весь путь точек
verbatim (форма, заданная пользователем, восстанавливается полностью, ADR-052).

**Файл:** `SmartCon.Core/Models/NetworkSnapshot.cs`

```csharp
public sealed record ElementSnapshot
{
    public ElementId ElementId { get; init; }
    public bool IsMepCurve { get; init; }
    public XYZ? FiOrigin/FiBasisX/FiBasisY/FiBasisZ { get; init; }  // FamilyInstance
    public XYZ? CurveStart/CurveEnd { get; init; }                  // MEPCurve (Line)
    public IReadOnlyList<XYZ>? FlexPoints { get; init; }            // FlexPipe — весь путь
    public XYZ? FirstConnectorOrigin { get; init; }                 // fallback-позиция
    public double ConnectorRadius { get; init; }
    public ElementId? FamilySymbolId { get; init; }
    public IReadOnlyDictionary<int, double> ConnectorRadii { get; init; }
    public IReadOnlyList<ConnectionRecord> Connections { get; init; }
}
```

---

## NetworkSnapshotStore

Хранилище снапшотов позиций элементов. Используется для отката цепочки к исходному состоянию.

**Файл:** `SmartCon.Core/Models/NetworkSnapshotStore.cs`

```csharp
public sealed class NetworkSnapshotStore { ... }
```

---

## LookupColumnConstraint

Ограничение колонки LookupTable для multi-column поиска.

**Файл:** `SmartCon.Core/Models/LookupColumnConstraint.cs`

```csharp
public sealed record LookupColumnConstraint(
    int ConnectorIndex,
    string ParameterName,
    double ValueMm
);
```

---

## ChainTopology

Enum всех поддерживаемых топологий соединения.

**Файл:** `SmartCon.Core/Models/ChainTopology.cs`

```csharp
public enum ChainTopology
{
    Direct,                // static <-> dynamic
    ReducerOnly,           // static <-> reducer <-> dynamic
    FittingOnly,           // static <-> fitting <-> dynamic
    ReducerFitting,        // static <-> reducer <-> fitting <-> dynamic
    FittingReducer,        // static <-> fitting <-> reducer <-> dynamic
    FittingChain,          // [Future] static <-> fitting1 <-> fitting2 <-> ...
    FittingChainReducer,   // [Future] multi-fitting + reducer
    ReducerFittingChain,   // [Future] reducer + multi-fitting
    ComplexChain           // [Future] reducer + multi-fitting + reducer
}
```

---

## FittingChainNodeType

Тип звена в цепочке фитингов.

**Файл:** `SmartCon.Core/Models/FittingChainNodeType.cs`

```csharp
public enum FittingChainNodeType { Fitting, Reducer }
```

---

## FittingChainLink

Одно звено в цепочке фитингов — фитинг или редьюсер с полными параметрами.

**Файл:** `SmartCon.Core/Models/FittingChainLink.cs`

```csharp
public sealed record FittingChainLink
{
    public required FittingChainNodeType Type { get; init; }
    public required FittingMappingRule Rule { get; init; }
    public required FittingMapping Family { get; init; }
    public required ConnectionTypeCode CtcIn { get; init; }
    public required ConnectionTypeCode CtcOut { get; init; }
    public required double RadiusIn { get; init; }
    public required double RadiusOut { get; init; }
}
```

---

## FittingChainPlan

Результат работы `IFittingChainResolver` — полный план цепочки соединения.

**Файл:** `SmartCon.Core/Models/FittingChainPlan.cs`

```csharp
public sealed class FittingChainPlan
{
    public required ConnectionTypeCode StaticCtc { get; init; }
    public required ConnectionTypeCode DynamicCtc { get; init; }
    public required double StaticRadius { get; init; }
    public required double DynamicRadius { get; init; }
    public required ChainTopology Topology { get; init; }
    public IReadOnlyList<FittingChainLink> Links { get; init; } = [];
    public bool IsDirect => Topology == ChainTopology.Direct;
    public bool HasReducer => Links.Any(l => l.Type == FittingChainNodeType.Reducer);
    public int FittingCount => Links.Count(l => l.Type == FittingChainNodeType.Fitting);
    public int ReducerCount => Links.Count(l => l.Type == FittingChainNodeType.Reducer);
}
```

---

## FamilyInfo

Информация о семействе фитинга, прошедшем фильтрацию по критериям PipeConnect (OST_PipeFitting + MultiPort + 2 коннектора). Используется в `IFittingFamilyRepository` и `FamilySelectorViewModel`.

**Файл:** `SmartCon.Core/Models/FamilyInfo.cs`

```csharp
public sealed record FamilyInfo(
    string FamilyName,
    string? PartTypeName,       // "MultiPort"
    int ConnectorCount,          // всегда 2 после фильтрации
    IReadOnlyList<string> SymbolNames  // доступные типоразмеры
);
```

---

## FamilySizeOption

Типоразмер динамического семейства для выбора в UI.

**Файл:** `SmartCon.Core/Models/FamilySizeOption.cs`

```csharp
public sealed class FamilySizeOption
{
    public string DisplayName { get; init; }
    public double Radius { get; init; }
    public bool IsAutoSelect { get; init; }
    public string Source { get; init; }
    public IReadOnlyList<double> AllConnectorRadii { get; init; }
}
```

---

## SizeOption

Доступный размер динамического семейства для выбора в UI.

**Файл:** `SmartCon.Core/Models/SizeOption.cs`

```csharp
public sealed record SizeOption
{
    public required string DisplayName { get; init; }
    public required double Radius { get; init; }
    public string Source { get; init; } = "FamilySymbol";
    public bool IsAutoSelect { get; init; }
    public override string ToString() => DisplayName;
}
```

---

## SizeTableRow

Строка из Revit FamilySizeTable (lookup table) или перечисления FamilySymbol. Содержит радиусы коннекторов, query-параметры и non-size параметры.

**Файл:** `SmartCon.Core/Models/SizeTableRow.cs`

```csharp
public sealed record SizeTableRow
{
    public required int TargetColumnIndex { get; init; }
    public required double TargetRadiusFt { get; init; }
    public required IReadOnlyDictionary<int, double> ConnectorRadiiFt { get; init; }
    public IReadOnlyList<double> QueryParameterRadiiFt { get; init; } = [];
    public int UniqueQueryParameterCount { get; init; } = 1;
    public IReadOnlyList<IReadOnlyList<int>> QueryParamConnectorGroups { get; init; } = [];
    public IReadOnlyDictionary<string, string> NonSizeParameterValues { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> QueryParamNames { get; init; } = [];
    public IReadOnlyList<double> QueryParamRawValuesMm { get; init; } = [];
}
```

---

## AllSizeRowsResult

Сводный результат выборки всех строк из всех таблиц семейства.

**Файл:** `SmartCon.Core/Models/SizeTableRow.cs`

```csharp
public sealed record AllSizeRowsResult(
    IReadOnlyList<SizeTableRow> Rows,
    IEnumerable<string> AllNonSizeParamNames,
    IReadOnlyList<IReadOnlyList<SizeTableRow>> PerTableRows,
    IEnumerable<long> ValidDnKeys);
```

---

## ConnectionRecord

Запись о соединении между двумя коннекторами. Хранится в ConnectionGraph при построении графа для последующего восстановления при откате.

**Файл:** `SmartCon.Core/Models/ConnectionRecord.cs`

```csharp
public sealed record ConnectionRecord(
    ElementId ThisElementId,
    int ThisConnectorIndex,
    ElementId NeighborElementId,
    int NeighborConnectorIndex
);
```

---

## CtcGuesser

Статические алгоритмы авто-определения CTC (ConnectionTypeCode) для фитингов и редьюсеров. Чистая логика без Revit API — покрыта юнит-тестами.

**Файл:** `SmartCon.Core/Models/CtcGuesser.cs`

```csharp
public static class CtcGuesser
{
    public static bool CanDirectConnect(
        ConnectionTypeCode left, ConnectionTypeCode right,
        IReadOnlyList<FittingMappingRule> rules);

    public static ConnectionTypeCode FindDirectConnectCounterpart(
        ConnectionTypeCode ctc, IReadOnlyList<FittingMappingRule> rules);

    public static (ConnectionTypeCode ForStatic, ConnectionTypeCode ForDynamic) GuessAdapterCtc(
        ConnectionTypeCode staticCTC, ConnectionTypeCode dynamicCTC,
        IReadOnlyList<FittingMappingRule> rules);

    public static (ConnectionTypeCode ForStaticSide, ConnectionTypeCode ForDynamicSide) GuessReducerCtc(
        ConnectionTypeCode staticCTC, ConnectionTypeCode dynamicCTC,
        IReadOnlyList<FittingMappingRule> rules);
}
```

---

## ExportMapping

Модель маппинга для экспорта: поле → исходное значение → целевое значение.

**Файл:** `SmartCon.Core/Models/ExportMapping.cs`

```csharp
public sealed record ExportMapping
{
    public string Field { get; init; } = string.Empty;
    public string SourceValue { get; init; } = string.Empty;
    public string TargetValue { get; init; } = string.Empty;
}
```

---

## ExportNameOverride

Переопределение имени файла при экспорте: словарь полей и значений.

**Файл:** `SmartCon.Core/Models/ExportNameOverride.cs`

```csharp
public sealed record ExportNameOverride
{
    public Dictionary<string, string> FieldValues { get; init; } = [];
}
```

---

## FamilySizeFormatter

Утилиты форматирования отображаемых имён типоразмеров семейств (DN, multi-DN).

**Файл:** `SmartCon.Core/Models/FamilySizeFormatter.cs`

```csharp
public static class FamilySizeFormatter
{
    public static string BuildDisplayName(
        IReadOnlyList<double> queryParamRadiiFt, int targetColumnIndex);

    public static string BuildDisplayNameLegacy(
        IReadOnlyDictionary<int, double> connectorRadiiFt, int targetConnectorIndex);

    public static string BuildAutoSelectDisplayName(
        IReadOnlyList<double> queryParamRadiiFt, int targetColumnIndex, string? symbolName = null);

    public static int ToDn(double radiusFt);
    public static double DnToRadiusFt(int dn);
    public static List<FamilySizeOption> DeduplicateFamilyOptions(List<FamilySizeOption> options);
    public static List<FamilySizeOption> AppendSymbolNameSuffix(List<FamilySizeOption> sorted);
}
```

---

## PipeConnectStateMachine

Статическая state machine для PipeConnect. Определяет допустимые переходы между состояниями PipeConnectState и терминальные состояния.

**Файл:** `SmartCon.Core/Models/PipeConnectStateMachine.cs`

```csharp
public static class PipeConnectStateMachine
{
    public static bool CanTransition(PipeConnectState from, PipeConnectState to);
    public static bool IsTerminal(PipeConnectState state);
}
```

---

## ElementIdEqualityComparer

Equality comparer для ElementId, использующий стабильное целочисленное значение вместо reference equality. Необходим, потому что в Revit 2025 ElementId — класс.

**Файл:** `SmartCon.Core/Models/ElementIdEqualityComparer.cs`

```csharp
public sealed class ElementIdEqualityComparer : IEqualityComparer<ElementId>
{
    public static readonly ElementIdEqualityComparer Instance = new();
    public bool Equals(ElementId? x, ElementId? y);
    public int GetHashCode(ElementId obj);
}
```

---

## ChainTraversalRunner

Драйвер обхода цепочки уровней («Подключить всё») с непробиваемой защитой от бесконечного цикла. Шаг обязан либо продвинуть глубину, либо вернуть null (ошибка) — иначе обход останавливается с причиной `NoProgress`. См. issue #137.

**Файл:** `SmartCon.Core/Services/ChainTraversalRunner.cs`

```csharp
public enum ChainTraversalStopReason { Completed, StepFailed, NoProgress }

public readonly record struct ChainTraversalResult(
    int FinalDepth, int Processed, ChainTraversalStopReason StopReason);

public static class ChainTraversalRunner
{
    public static ChainTraversalResult Run(
        int startDepth, int targetLevel, int maxLevel, Func<int?> step);
}
```

---

## PipeAdjustOp

Дельты концов кривой прямой трубы для per-level гашения смещения (ADR-052).
Результат `PipeLengthAbsorber.Compute`. Применяется как
`Line.CreateBound(start + StartDelta, end + EndDelta)`.
Элемент идентифицируется сырым `long` — тестируемость без Revit runtime.
Все величины в Internal Units (decimal feet, I-02).

**Файл:** `SmartCon.Core/Models/PipeAdjustOp.cs`

```csharp
public sealed record PipeAdjustOp
{
    public required long ElementId { get; init; }
    public required Vec3 StartDelta { get; init; }      // дельта endpoint 0
    public required Vec3 EndDelta { get; init; }        // дельта endpoint 1
    public required double AbsorbedLengthFt { get; init; }  // >0 укорочение, <0 удлинение, 0 трансляция
}
```
