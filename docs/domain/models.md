# Доменные модели

> Загружать: при работе с моделями данных.
> **Правило:** Не создавай новые доменные классы без добавления их в этот файл.

Все модели живут в `SmartCon.Core/Models/`. Используют только типы из .NET и собственные типы Core.
Исключение: `ElementId`, `XYZ`, `Domain`, `BuiltInParameter`, `ForgeTypeId` — это value-типы Revit, допустимые в Core через compile-time ссылку на API (без runtime-зависимости, I-09).

Для чистой математики (VectorUtils, ConnectorAligner) используется `Vec3` вместо `XYZ` (ADR-009). Конвертация `XYZ ↔ Vec3` — в `SmartCon.Revit/Extensions/XYZExtensions.cs`.

---

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
    public bool IsFree { get; init; }                 // AllRefs.IsEmpty == true
}
```

---

## ConnectionTypeCode

Строго типизированная обёртка над кодом из поля `Connector.Description`.

**Файл:** `SmartCon.Core/Models/ConnectionTypeCode.cs`

```csharp
public readonly record struct ConnectionTypeCode(int Value)
{
    public static readonly ConnectionTypeCode Undefined = new(0);
    public bool IsDefined => Value != 0;
    public override string ToString() => Value.ToString();

    public static ConnectionTypeCode Parse(string? raw) =>
        int.TryParse(raw, out var v) && v != 0 ? new(v) : Undefined;
}
```

---

## ConnectorTypeDefinition

Определение типа коннектора для пользовательского справочника. Хранится в JSON (AppData).

**Файл:** `SmartCon.Core/Models/ConnectorTypeDefinition.cs`

```csharp
public sealed record ConnectorTypeDefinition
{
    public int Code { get; init; }
    public string Name { get; init; } = string.Empty;        // "Сварка", "Резьба"
    public string Description { get; init; } = string.Empty;  // подробное описание
}
```

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

## FamilyInfo *(Phase 3C)*

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

## AlignmentResult *(Phase 2)*

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

## FittingCardItem

Элемент списка фитингов/переходников в UI.

**Файл:** `SmartCon.PipeConnect/ViewModels/FittingCardItem.cs`

```csharp
public sealed class FittingCardItem
{
    public string DisplayName { get; init; }
    public bool IsDirectConnect { get; init; }
    public FittingMapping? PrimaryFitting { get; init; }
    public FittingMappingRule? Rule { get; init; }
}
```

---

## DynamicSizeLoadResult

Результат загрузки динамических типоразмеров.

**Файл:** `SmartCon.PipeConnect/Services/DynamicSizeLoader.cs`

```csharp
public sealed record DynamicSizeLoadResult(
    IReadOnlyList<FamilySizeOption> Sizes,
    FamilySizeOption? DefaultSelection,
    bool HasSizeOptions
);
```

---

## ConnectorCycleState

Состояние циклического перебора коннекторов.

**Файл:** `SmartCon.PipeConnect/Services/ConnectorCycleService.cs`

```csharp
public sealed class ConnectorCycleState
{
    public int Count { get; }
    public int CurrentIndex { get; }
    public ConnectorProxy? FindNext();
    public void Initialize(IReadOnlyList<ConnectorProxy> connectors, ConnectorProxy active);
}
```

---

## ConnectOperationContext

Контекст операции соединения, передаваемый в ConnectExecutor.

**Файл:** `SmartCon.PipeConnect/Services/ConnectExecutor.cs`

```csharp
public sealed class ConnectOperationContext
{
    public required Document Doc { get; init; }
    public required ITransactionGroupSession GroupSession { get; init; }
    public required PipeConnectSessionContext Session { get; init; }
    public required VirtualCtcStore VirtualCtcStore { get; init; }
}
```

---

## NetworkSnapshot / NetworkSnapshotStore

Снапшот позиции элемента для отката цепочки.

**Файл:** `SmartCon.Core/Models/NetworkSnapshot.cs`, `NetworkSnapshotStore.cs`

```csharp
public sealed record NetworkSnapshot(ElementId ElementId, XYZ OriginalOrigin);
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
