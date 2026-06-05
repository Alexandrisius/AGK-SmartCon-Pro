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

Определение типа коннектора для пользовательского справочника. Хранится per-project
в `ExtensibleStorage.DataStorage` активного `Document` ([ADR-012](../adr/012-per-project-extensible-storage.md)).

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

---

## ChainTopology

Enum всех поддерживаемых топологий соединения.

**Файл:** `SmartCon.Core/Models/ChainTopology.cs`

```csharp
public enum ChainTopology
{
    Direct,                // static ↔ dynamic
    ReducerOnly,           // static ↔ reducer ↔ dynamic
    FittingOnly,           // static ↔ fitting ↔ dynamic
    ReducerFitting,        // static ↔ reducer ↔ fitting ↔ dynamic
    FittingReducer,        // static ↔ fitting ↔ reducer ↔ dynamic
    FittingChain,          // [Future] static ↔ fitting1 ↔ fitting2 ↔ ...
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

## ShareProjectSettings *(ProjectManagement)*

Корневая модель настроек модуля ShareProject. Хранится per-project в ExtensibleStorage (ADR-013).

**Файл:** `SmartCon.Core/Models/ShareProjectSettings.cs`

```csharp
public sealed record ShareProjectSettings
{
    public string ShareFolderPath { get; init; } = string.Empty;
    public FileNameTemplate FileNameTemplate { get; init; } = new();
    public List<FieldDefinition> FieldLibrary { get; init; } = [];
    public PurgeOptions PurgeOptions { get; init; } = new();
    public List<string> KeepViewNames { get; init; } = [];
    public bool SyncBeforeShare { get; init; } = true;
    public static ShareProjectSettings Empty => new();
}
```

---

## FileNameTemplate *(ProjectManagement)*

Шаблон разбора имени файла: разделитель + блоки + маппинг статусов.

**Файл:** `SmartCon.Core/Models/FileNameTemplate.cs`

```csharp
public sealed record FileNameTemplate
{
    public string Delimiter { get; init; } = "-";
    public List<FileBlockDefinition> Blocks { get; init; } = [];
    public List<StatusMapping> StatusMappings { get; init; } = [];
}
```

---

## FileBlockDefinition *(ProjectManagement)*

Определение блока имени файла с ролью и меткой.

**Файл:** `SmartCon.Core/Models/FileBlockDefinition.cs`

```csharp
public sealed record FileBlockDefinition
{
    public int Index { get; init; }
    public string Field { get; init; } = "";
    public string Label { get; init; } = "";
    public ParseRule? ParseRule { get; init; }
    public List<string> AllowedValues { get; init; } = [];
}
```

---

## StatusMapping *(ProjectManagement)*

Пара значений статуса: WIP → Shared.

**Файл:** `SmartCon.Core/Models/StatusMapping.cs`

```csharp
public sealed record StatusMapping
{
    public string WipValue { get; init; } = "";     // "S0"
    public string SharedValue { get; init; } = "";  // "S1"
}
```

---

## FieldDefinition *(ProjectManagement)*

Определение поля из библиотеки — переиспользуемое описание с валидацией.

**Файл:** `SmartCon.Core/Models/FieldDefinition.cs`

```csharp
public sealed record FieldDefinition
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public ValidationMode ValidationMode { get; init; }
    public List<string> AllowedValues { get; init; } = [];
    public int? MinCharCount { get; init; }
    public int? MaxCharCount { get; init; }
}
```

---

## PurgeOptions *(ProjectManagement)*

Настраиваемый список категорий очистки для Shared-файла.

**Файл:** `SmartCon.Core/Models/PurgeOptions.cs`

```csharp
public sealed record PurgeOptions
{
    public bool PurgeRvtLinks { get; init; } = true;
    public bool PurgeCadImports { get; init; } = true;
    public bool PurgeImages { get; init; } = true;
    public bool PurgePointClouds { get; init; } = true;
    public bool PurgeGroups { get; init; } = true;
    public bool PurgeAssemblies { get; init; } = true;
    public bool PurgeSpaces { get; init; } = true;
    public bool PurgeRebar { get; init; } = true;
    public bool PurgeFabricReinforcement { get; init; } = true;
    public bool PurgeUnused { get; init; } = true;
}
```

---

## ShareProjectResult *(ProjectManagement)*

Результат операции ShareProject.

**Файл:** `SmartCon.Core/Models/ShareProjectResult.cs`

```csharp
public sealed record ShareProjectResult
{
    public bool Success { get; init; }
    public string? SharedFilePath { get; init; }
    public double ElapsedSeconds { get; init; }
    public string? ErrorMessage { get; init; }
    public int ElementsDeleted { get; init; }
    public int PurgedElementsCount { get; init; }
}
```

---

## ViewInfo *(ProjectManagement)*

Информация о виде для отображения в UI настроек.

**Файл:** `SmartCon.Core/Models/ViewInfo.cs`

```csharp
public sealed class ViewInfo
{
    public string Name { get; init; } = string.Empty;
    public ElementId Id { get; init; }
    public string ViewType { get; init; } = string.Empty;
}
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

## IFittingCtcSetupItem

Абстракция для данных настройки CTC коннекторов фитинга, используемая UI-слоем. UI предоставляет реализацию с INotifyPropertyChanged.

**Файл:** `SmartCon.Core/Models/IFittingCtcSetupItem.cs`

```csharp
public interface IFittingCtcSetupItem
{
    int ConnectorIndex { get; }
    string ParameterName { get; }
    double DiameterMm { get; }
    ConnectorTypeDefinition? PreSelectedType { get; }
    ConnectorTypeDefinition? SelectedType { get; set; }
    string DisplayText { get; }
}
```

---

## ParseMode

Режим парсинга сегмента имени файла.

**Файл:** `SmartCon.Core/Models/ParseMode.cs`

```csharp
public enum ParseMode
{
    DelimiterSegment,
    FixedWidth,
    BetweenMarkers,
    AfterMarker,
    Remainder
}
```

---

## ParseRule

Правило парсинга блока имени файла: режим, разделитель, индексы, маркеры.

**Файл:** `SmartCon.Core/Models/ParseRule.cs`

```csharp
public sealed record ParseRule
{
    public ParseMode Mode { get; init; } = ParseMode.DelimiterSegment;
    public string Delimiter { get; init; } = "-";
    public int SegmentIndex { get; init; } = 1;
    public int SegmentCount { get; init; } = 1;
    public int CharOffset { get; init; }
    public int CharCount { get; init; }
    public string OpenMarker { get; init; } = "(";
    public int OpenMarkerIndex { get; init; } = 1;
    public string CloseMarker { get; init; } = ")";
    public int CloseMarkerIndex { get; init; } = 1;
    public string Marker { get; init; } = "-";
    public int MarkerIndex { get; init; } = 1;

    public static ParseRule DefaultDelimiter(string delimiter = "-", int segmentIndex = 1);
}
```

---

## PendingUpdate / MultiVersionPendingUpdate / StagedArtifact

Описание подготовленного обновления, ожидающего установки при следующем закрытии Revit. MultiVersionPendingUpdate поддерживает несколько версий Revit.

**Файл:** `SmartCon.Core/Models/PendingUpdate.cs`

```csharp
public sealed record PendingUpdate(
    string Version,
    string StagingPath,
    DateTime StagedAt,
    string TargetInstallPath
);

public sealed record MultiVersionPendingUpdate(
    string Version,
    DateTime StagedAt,
    List<StagedArtifact> Artifacts
);

public sealed record StagedArtifact(
    string StagingPath,
    string TargetInstallPath,
    string ArtifactTag
);
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

## SemVersion

Лёгкий парсер и компаратор семантических версий (SemVer 2.0.0 subset). Без внешних зависимостей. Поддерживает pre-release метки.

**Файл:** `SmartCon.Core/Models/SemVersion.cs`

```csharp
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? Metadata { get; }
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public SemVersion(int major, int minor, int patch, string? prerelease = null, string? metadata = null);
    public static SemVersion Parse(string version);
    public static bool TryParse(string version, [NotNullWhen(true)] out SemVersion? result);
    public int CompareTo(SemVersion? other);
    public override string ToString();
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

## SizeTableRow / AllSizeRowsResult

Строка из Revit FamilySizeTable (lookup table) или перечисления FamilySymbol. Содержит радиусы коннекторов, query-параметры и non-size параметры.

**Файл:** `SmartCon.Core/Models/SizeTableRow.cs`

```csharp
public sealed record AllSizeRowsResult(
    IReadOnlyList<SizeTableRow> Rows,
    IEnumerable<string> AllNonSizeParamNames,
    IReadOnlyList<IReadOnlyList<SizeTableRow>> PerTableRows,
    IEnumerable<long> ValidDnKeys);

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

## UpdateInfo

Метаданные GitHub Release, который новее текущей версии плагина.

**Файл:** `SmartCon.Core/Models/UpdateInfo.cs`

```csharp
public sealed record UpdateInfo(
    string Version,
    string TagName,
    string? ReleaseNotes,
    DateTime PublishedAt,
    string DownloadUrl,
    long FileSize,
    string AssetName,
    string? Changelog = null
);
```

---

## UpdateSettings

Пользовательские настройки системы автообновления через GitHub.

**Файл:** `SmartCon.Core/Models/UpdateSettings.cs`

```csharp
public sealed record UpdateSettings(
    bool CheckOnStartup,
    string? GitHubToken,
    string GitHubOwner,
    string GitHubRepo,
    bool IncludePrerelease
)
{
    public static UpdateSettings Default => new(
        CheckOnStartup: true,
        GitHubToken: null,
        GitHubOwner: "Alexandrisius",
        GitHubRepo: "AGK-SmartCon-Pro",
        IncludePrerelease: false
    );
}
```

---

## ValidationMode

Режим валидации значения поля.

**Файл:** `SmartCon.Core/Models/ValidationMode.cs`

```csharp
public enum ValidationMode
{
    None,
    AllowedValues,
    CharCount
}
```

---

## ValidationResult / BlockValidation

Результат валидации имени файла: общий статус, сводка и список результатов по блокам.

**Файл:** `SmartCon.Core/Models/ValidationResult.cs`

```csharp
public sealed record BlockValidation(
    int Index,
    string Field,
    string Value,
    bool IsValid,
    string? Error
);

public sealed record ValidationResult(
    bool IsValid,
    string Summary,
    List<BlockValidation> Blocks
);
```

---

## FormulaEngine (AST Parser)

AST-парсер и решатель формул Revit (ADR-005). Все типы — internal, живут в `SmartCon.Core/Math/FormulaEngine/`.

### Token / TokenType / Tokenizer

Токен лексера, его тип (number, identifier, operator, etc.) и лексер (разбор строки формулы в список токенов).

**Файлы:** `FormulaEngine/Token.cs`, `TokenType.cs`, `Tokenizer.cs`

### AstNode

Базовый класс узла AST.

**Файл:** `FormulaEngine/Ast/AstNode.cs`

### NumberNode / VariableNode / UnaryOpNode / BinaryOpNode / FunctionCallNode / IfNode / SizeLookupNode

Узлы AST: число, переменная, унарная/бинарная операция, вызов функции, if-выражение, size_lookup.

**Файлы:** `FormulaEngine/Ast/*.cs`

### UnaryOp / BinaryOp

Перечисления операторов (Plus, Minus, Multiply, Divide, And, Or, etc.).

**Файлы:** `FormulaEngine/Ast/UnaryOp.cs`, `BinaryOp.cs`

### Parser

Рекурсивный спуск-парсер формул Revit. Преобразует токены в AST.

**Файл:** `FormulaEngine/Parser.cs`

### Evaluator

Интерпретатор AST — вычисляет значение при заданных параметрах.

**Файл:** `FormulaEngine/Evaluator.cs`

### FormulaSolver

Единая точка входа: Evaluate + SolveFor (ADR-005). Использует IfSimplifier → AlgebraicInverter → BisectionSolver.

**Файл:** `FormulaEngine/Solver/FormulaSolver.cs`

### IfSimplifier

Упрощает if()-ветки при известных условиях.

**Файл:** `FormulaEngine/Solver/IfSimplifier.cs`

### AlgebraicInverter

Алгебраическая инверсия простых формул (a = b + c → b = a - c).

**Файл:** `FormulaEngine/Solver/AlgebraicInverter.cs`

### BisectionSolver

Численное решение методом бисекции для нелинейных формул.

**Файл:** `FormulaEngine/Solver/BisectionSolver.cs`

### VariableExtractor

Извлекает список переменных из формулы.

**Файл:** `FormulaEngine/Solver/VariableExtractor.cs`

### SizeLookupParser

Парсинг size_lookup(...) выражений.

**Файл:** `FormulaEngine/SizeLookupParser.cs`

### UnitStripper

Удаление единиц измерения из строки формулы (mm, m, etc.).

**Файл:** `FormulaEngine/UnitStripper.cs`

### FormulaParseException

Исключение при ошибке парсинга формулы.

**Файл:** `FormulaEngine/FormulaParseException.cs`

---

## Math Utilities

Вспомогательные математические классы в `SmartCon.Core/Math/`.

### ConnectorAligner

Вычисление матриц поворота и смещения для выравнивания коннекторов (ADR-009, Vec3).

**Файл:** `Math/ConnectorAligner.cs`

### VectorUtils

Базовые векторные операции с Vec3.

**Файл:** `Math/VectorUtils.cs`

### BestSizeMatcher

Подбор ближайшего доступного типоразмера из LookupTable.

**Файл:** `Math/BestSizeMatcher.cs`

### SizeRowSymbolMatcher

Сопоставление строки типоразмера с символом DN.

**Файл:** `Math/SizeRowSymbolMatcher.cs`

### LookupTableCsvParser

Парсинг CSV LookupTable семейства Revit.

**Файл:** `Math/LookupTableCsvParser.cs`

---

## FamilyManager Models

Модели модуля FamilyManager (Phase 13–17: Published Storage → Attribute Extraction). Все модели — immutable records, живут в `SmartCon.Core/Models/FamilyManager/`.
Идентификаторы — `string` (GUID), даты — `DateTimeOffset`.

### CatalogProviderKind

Тип провайдера каталога (локальный, сетевой, облачный).

**Файл:** `CatalogProviderKind.cs`

```csharp
public enum CatalogProviderKind
{
    Local,
    Network,
    Cloud
}
```

---

### AttributeDefinition

Определение атрибута (параметра) для извлечения из семейств. Заменяет устаревший `AttributePreset` (ADR-017).

**Файл:** `AttributeDefinition.cs`

```csharp
public sealed record AttributeDefinition(
    string Id,
    string Name,
    string? Group,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);
```

---

### CategoryAttributeBinding

Связь категории с атрибутом — какие параметры извлекать для семейств данной категории.

**Файл:** `CategoryAttributeBinding.cs`

```csharp
public sealed record CategoryAttributeBinding(
    string Id,
    string CategoryId,
    string AttributeId,
    int SortOrder,
    bool IsEnabled);
```

---

### EffectiveCategoryAttribute

Эффективный атрибут категории с учётом наследования от родительских категорий.

**Файл:** `EffectiveCategoryAttribute.cs`

```csharp
public sealed record EffectiveCategoryAttribute(
    string AttributeId,
    string Name,
    string? Group,
    int SortOrder,
    bool IsEnabled,
    bool IsInherited,
    string? SourceCategoryId);
```

---

### FamilyMetadataPackage

Пакет метаданных для импорта/экспорта категорий, атрибутов и связей.

**Файл:** `FamilyMetadataPackage.cs`

```csharp
public sealed class FamilyMetadataPackage
{
    public string Format { get; init; } = "smartcon.familymanager.metadata-package";
    public int Version { get; init; } = 2;
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public FamilyMetadataPackageSections Sections { get; init; } = new();
    public List<FamilyMetadataCategoryNode> Categories { get; init; } = [];
    public List<FamilyMetadataAttribute> Attributes { get; init; } = [];
    public List<FamilyMetadataBinding> Bindings { get; init; } = [];
}

public sealed class FamilyMetadataPackageSections
{
    public bool Categories { get; init; }
    public bool Attributes { get; init; }
    public bool Bindings { get; init; }
}

public sealed class FamilyMetadataCategoryNode
{
    public string Name { get; init; } = string.Empty;
    public List<FamilyMetadataCategoryNode> Children { get; init; } = [];
}

public sealed class FamilyMetadataAttribute
{
    public string Name { get; init; } = string.Empty;
    public string? Group { get; init; }
}

public sealed class FamilyMetadataBinding
{
    public string CategoryPath { get; init; } = string.Empty;
    public string AttributeName { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public bool IsEnabled { get; init; } = true;
}
```

---

### FamilyDataImportRun

Запись о запуске импорта данных семейств (извлечение атрибутов из `.rfa`).

**Файл:** `FamilyDataImportRun.cs`

```csharp
public sealed record FamilyDataImportRun(
    string Id,
    string CatalogItemId,
    string? VersionId,
    FamilyDataImportStatus Status,
    string? ErrorMessage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);
```

---

### FamilyDataImportStatus

Статус импорта данных семейства.

**Файл:** `FamilyDataImportStatus.cs`

```csharp
public enum FamilyDataImportStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}
```

---

### ExtractedAttributeValue

Извлечённое значение атрибута из семейства.

**Файл:** `ExtractedAttributeValue.cs`

```csharp
public sealed record ExtractedAttributeValue(
    string Id,
    string CatalogItemId,
    string? VersionId,
    string AttributeId,
    string? TypeId,
    string? ValueText,
    AttributeValueStatus Status,
    DateTimeOffset ExtractedAtUtc);
```

---

### AttributeValueStatus

Статус извлечённого значения.

**Файл:** `AttributeValueStatus.cs`

```csharp
public enum AttributeValueStatus
{
    Valid,
    Missing,
    Error
}
```

---

### AttributeScope

Область применения атрибута (тип/экземпляр).

**Файл:** `AttributeScope.cs`

```csharp
public enum AttributeScope
{
    Type,
    Instance
}
```

---

---

### ContentStatus

Статус опубликованного контента в каталоге. FM — Published-зона, всё импортированное = опубликовано.

**Файл:** `ContentStatus.cs`

```csharp
public enum ContentStatus
{
    Active = 0,       // доступно для загрузки в проекты
    Deprecated = 1,   // устарело, не рекомендуется для новых проектов
    Retired = 2       // снято с публикации, недоступно для загрузки
}
```

---

### FamilyAssetType

Тип вспомогательного ассета (изображение, документ и т.д.), прикреплённого к семейству.

**Файл:** `FamilyAssetType.cs`

```csharp
public enum FamilyAssetType
{
    Image = 0,
    Video = 1,
    Document = 2,
    Model3D = 3,
    LookupTable = 4,
    Other = 5,
    Spreadsheet = 6
}
```

---

### DatabaseConnection

Подключение к базе данных каталога по пути. Папка содержит `catalog.db` (SQLite) + `files/` (managed storage).

**Файл:** `DatabaseConnection.cs`

```csharp
public sealed record DatabaseConnection(
    string Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAtUtc);
```

---

### DatabaseConnectionRegistry

Реестр подключений. Сохраняется как `registry.json` в `%APPDATA%\SmartCon\FamilyManager\`.

**Файл:** `DatabaseConnectionRegistry.cs`

```csharp
public sealed record DatabaseConnectionRegistry(
    string? ActiveConnectionId,
    IReadOnlyList<DatabaseConnection> Connections);
```

---

### FamilyCatalogItem

Логическая запись каталога семейств — основная сущность, к которой привязаны версии и файлы.

**Файл:** `FamilyCatalogItem.cs`

```csharp
public sealed record FamilyCatalogItem(
    string Id,
    string Name,
    string NormalizedName,
    string? Description,
    string? CategoryPath,
    string? CategoryId,
    string? Manufacturer,
    ContentStatus ContentStatus,
    string? CurrentVersionLabel,
    IReadOnlyList<string> Tags,
    string? PublishedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

---

### FamilyCatalogVersion

Версия записи каталога — связка с конкретным файлом `.rfa`. Один CatalogItem может иметь несколько версий.

**Файл:** `FamilyCatalogVersion.cs`

```csharp
public sealed record FamilyCatalogVersion(
    string Id,
    string CatalogItemId,
    string FileId,
    string VersionLabel,
    string Sha256,
    int RevitMajorVersion,
    int? TypesCount,
    int? ParametersCount,
    DateTimeOffset PublishedAtUtc);
```

---

### FamilyFileRecord

Физический файл семейства в managed storage. Путь: `{db-root}/files/{family-id}/{version}/r{revit}/{sha256}.rfa`.

**Файл:** `FamilyFileRecord.cs`

```csharp
public sealed record FamilyFileRecord(
    string Id,
    string RelativePath,
    string FileName,
    long SizeBytes,
    string Sha256,
    int RevitMajorVersion,
    DateTimeOffset ImportedAtUtc);
```

---

### FamilyAsset

Вспомогательный ассет (изображение, документ, lookup table), привязанный к семейству.
Файлы хранятся в `{db-root}/files/{family-id}/{version}/assets/{type}/`.

**Файл:** `FamilyAsset.cs`

```csharp
public sealed record FamilyAsset(
    string Id,
    string CatalogItemId,
    string? VersionLabel,
    FamilyAssetType AssetType,
    string FileName,
    string RelativePath,
    long SizeBytes,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    bool IsPrimary = false);
```

---

### AttributePreset

Набор параметров для извлечения из семейств, привязанный к категории. Дочерние категории наследуют параметры от родительских.

**Файл:** `AttributePreset.cs`

```csharp
public sealed record AttributePreset(
    string Id,
    string? CategoryId,
    IReadOnlyList<AttributePresetParameter> Parameters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

---

### AttributePresetParameter

Описание одного параметра в пресете.

**Файл:** `AttributePresetParameter.cs`

```csharp
public sealed record AttributePresetParameter(
    string ParameterName,
    string? DisplayName,
    int SortOrder)
{
    public string DisplayText => DisplayName ?? ParameterName;
}
```

---

### CategoryNode

Узел дерева категорий. Формирует иерархию категорий каталога семейств.

**Файл:** `CategoryNode.cs`

```csharp
public sealed record CategoryNode(
    string Id,
    string Name,
    string? ParentId,
    int SortOrder,
    string FullPath,
    DateTimeOffset CreatedAtUtc);
```

---

### CategoryTree

Иммутабельное дерево категорий с быстрым поиском по ID и дочерним узлам.

**Файл:** `CategoryTree.cs`

```csharp
public sealed class CategoryTree
{
    public CategoryTree(IReadOnlyList<CategoryNode> nodes);
    public IReadOnlyList<CategoryNode> GetAllNodes();
    public CategoryNode? GetById(string id);
    public IReadOnlyList<CategoryNode> GetChildren(string? parentId);
    public IReadOnlyList<CategoryNode> GetRootNodes();
    public string GetFullPath(string id);
    public IReadOnlyList<string> GetDescendantIds(string categoryId);
    public string BuildFullPath(string id);
}
```

---

### FamilyUpdateRequest

Запрос на обновление файла семейства в каталоге.

**Файл:** `FamilyUpdateRequest.cs`

```csharp
public sealed record FamilyUpdateRequest(
    string CatalogItemId,
    string FilePath,
    int RevitMajorVersion,
    string? CategoryId = null,
    string? CategoryName = null,
    string? FileName = null,
    string? OriginalSourcePath = null);
```

`OriginalSourcePath` (см. ADR-024) — путь к исходному `.rfa` до
копирования в temp staging folder. Используется для поиска Type
Catalog sidecar рядом с оригиналом.

---

### FamilyCatalogQuery

Параметры запроса поиска по каталогу с пагинацией и фильтрацией.

**Файл:** `FamilyCatalogQuery.cs`

```csharp
public sealed record FamilyCatalogQuery(
    string? SearchText,
    string? CategoryFilter,
    ContentStatus? StatusFilter,
    IReadOnlyList<string>? Tags,
    string? ManufacturerFilter,
    FamilyCatalogSort Sort,
    int Offset,
    int Limit);
```

---

### FamilyCatalogSort

Порядок сортировки результатов поиска по каталогу.

**Файл:** `FamilyCatalogSort.cs`

```csharp
public enum FamilyCatalogSort
{
    NameAsc,
    NameDesc,
    UpdatedAtDesc,
    CreatedAtDesc
}
```

---

### FamilyCatalogCapabilities

Описание возможностей провайдера каталога — используется для адаптации UI.

**Файл:** `FamilyCatalogCapabilities.cs`

```csharp
public sealed record FamilyCatalogCapabilities(
    bool SupportsWrite,
    bool SupportsSearch,
    bool SupportsTags,
    bool SupportsBatchImport,
    bool SupportsVersionHistory,
    CatalogProviderKind ProviderKind);
```

---

### FamilyImportRequest

Запрос на импорт одного файла `.rfa` в каталог. Файл копируется в managed storage.

**Файл:** `FamilyImportRequest.cs`

```csharp
public sealed record FamilyImportRequest(
    string FilePath,
    int RevitMajorVersion,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description,
    string? CategoryId = null,
    string FamilySource = "loadable",
    string? RevitCategory = null,
    string? FileName = null,
    string? OriginalSourcePath = null);
```

`OriginalSourcePath` — путь к исходному `.rfa` до его копирования в
temp staging folder (используется пайплайном «Импорт активного
файла»). Передаётся в `ImportTypeCatalogIfPresentAsync` для поиска
Type Catalog sidecar (.txt) рядом с оригиналом, когда рядом с temp
копией его нет. См. ADR-024.

### ActiveFamilyPreparationResult

Результат подготовки активного Revit family-документа для импорта.
Возвращается `IActiveFamilyFilePreparer.PrepareActiveFamilyAsync`.

**Файл:** `ActiveFamilyPreparationResult.cs`

```csharp
public sealed record ActiveFamilyPreparationResult(
    string TempRfaPath,
    string? TempTxtPath,
    string? OriginalRfaPath,
    string? OriginalTxtPath);
```

| Поле | Описание |
|---|---|
| `TempRfaPath` | Absolute path к `.rfa`, сохранённому в temp (например `%TEMP%\SmartCon\FMLoad\{guid}\{name}.rfa`) |
| `TempTxtPath` | Absolute path к скопированному sidecar `.txt` рядом с `TempRfaPath`, или `null` если sidecar не найден |
| `OriginalRfaPath` | Absolute path к исходному `.rfa` (managed storage или рабочая папка пользователя); `null` для несохранённых документов |
| `OriginalTxtPath` | Absolute path к исходному sidecar `.txt` рядом с `OriginalRfaPath`, или `null` |

---

### FamilyImportResult

Результат импорта одного файла — содержит ID созданных сущностей или флаг дубликата.

**Файл:** `FamilyImportResult.cs`

```csharp
public sealed record FamilyImportResult(
    bool Success,
    string? CatalogItemId,
    string? VersionId,
    string? FileId,
    string? FileName,
    string? VersionLabel,
    string? ErrorMessage,
    bool WasSkippedAsDuplicate = false,
    bool WasNewVersion = false);
```

---

### FamilyBatchImportResult

Агрегированный результат импорта нескольких файлов.

**Файл:** `FamilyBatchImportResult.cs`

```csharp
public sealed record FamilyBatchImportResult(
    IReadOnlyList<FamilyImportResult> Results,
    int TotalFiles,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
```

### FamilyBatchImportItem

Одна строка (файл) в диалоге пакетного импорта. Содержит метаданные файла, статус и выбранное пользователем действие.

**Файл:** `FamilyBatchImportItem.cs`

```csharp
public sealed record FamilyBatchImportItem(
    string FilePath,
    string FileName,
    string Sha256,
    int RevitMajorVersion,
    long FileSizeBytes,
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? TargetCategoryId = null,
    string? TargetCategoryName = null,
    string FamilySource = "loadable",
    int TypeCount = 0,
    string? RevitCategory = null,
    string? OriginalSourcePath = null)
{
    public FamilyBatchImportAction Action { get; set; }
    public string? TargetCategoryId { get; set; }
    public string? TargetCategoryName { get; set; }
}
```

`OriginalSourcePath` (см. ADR-024) — пробрасывается из
`ActiveFamilyPreparationResult` в пакетный импорт, чтобы
`ImportTypeCatalogIfPresentAsync` мог найти Type Catalog рядом с
исходным `.rfa`, когда рядом с temp-копией его нет.

---

### FamilyBatchImportStatus

Статус файла в диалоге пакетного импорта. Определяется на основе сравнения SHA256 с каталогом.

**Файл:** `FamilyBatchImportStatus.cs`

```csharp
public enum FamilyBatchImportStatus
{
    New,        // Новое семейство, отсутствует в каталоге
    Existing,   // Семейство есть в каталоге, но SHA256 отличается
    Duplicate,  // Точное совпадение SHA256 — пропускается автоматически
    Error       // Ошибка чтения файла
}
```

---

### FamilyBatchImportAction

Действие, выбранное пользователем для файла в пакетном импорте.

**Файл:** `FamilyBatchImportAction.cs`

```csharp
public enum FamilyBatchImportAction
{
    IncrementVersion,  // Создать новую версию (vN+1), обновить current_version_label
    OverwriteCurrent,  // Заменить файл текущей версии без изменения current_version_label
    Skip               // Пропустить файл
}
```

---

### FamilyFolderImportRequest

Запрос на импорт всех `.rfa` файлов из папки (с возможностью рекурсивного обхода).

**Файл:** `FamilyFolderImportRequest.cs`

```csharp
public sealed record FamilyFolderImportRequest(
    string FolderPath,
    int RevitMajorVersion,
    bool Recursive,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description,
    string? CategoryId = null);
```

---

### FamilyImportProgress

Прогресс пакетного импорта — передаётся в callback для обновления UI.

**Файл:** `FamilyImportProgress.cs`

```csharp
public sealed record FamilyImportProgress(
    int CurrentFileIndex,
    int TotalFiles,
    string CurrentFileName,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
```

---

### FamilyLoadOptions

Параметры загрузки семейства в проект Revit.

**Файл:** `FamilyLoadOptions.cs`

```csharp
public sealed record FamilyLoadOptions(
    bool OverwriteExisting = false,
    bool UpdateFamilyIfChanged = false,
    string? PreferredName = null)
{
    public static FamilyLoadOptions Default { get; } = new();
}
```

---

### FamilyLoadResult

Результат загрузки семейства в проект Revit.

**Файл:** `FamilyLoadResult.cs`

```csharp
public sealed record FamilyLoadResult(
    bool Success,
    string? FamilyName,
    string? Message,
    string? ErrorMessage);
```

---

### FamilyLoadStatus

Статус загрузки семейства в проект Revit.

**Файл:** `FamilyLoadStatus.cs`

```csharp
public enum FamilyLoadStatus
{
    Failed,
    Loaded,
    Updated,
    Current
}
```

---

### FamilyResolvedFile

Разрешённый путь к файлу `.rfa` — готов для загрузки в Revit.

**Файл:** `FamilyResolvedFile.cs`

```csharp
public sealed record FamilyResolvedFile(
    string AbsolutePath,
    string? CatalogItemId,
    string? VersionId,
    string? VersionLabel = null);
```

---

### FamilyPlacementDragData

Payload для drag-and-drop размещения типоразмера семейства из FamilyManager в canvas Revit.

**Файл:** `FamilyPlacementDragData.cs`

```csharp
public sealed record FamilyPlacementDragData(
    string CatalogItemId,
    string FamilyName,
    string TypeName,
    int TargetRevitVersion,
    bool IsVirtual = false);
```

---

### TypeCatalogEntry

Одна запись (тип) из Type Catalog (.txt) семейства Revit.

**Файл:** `TypeCatalogEntry.cs`

```csharp
public sealed record TypeCatalogEntry(
    string TypeName,
    IReadOnlyDictionary<string, string> ParameterValues);
```

---

### TypeCatalogParseResult

Результат парсинга Type Catalog (.txt) семейства Revit.

**Файл:** `TypeCatalogParseResult.cs`

```csharp
public sealed record TypeCatalogParseResult(
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<TypeCatalogEntry> Entries)
{
    public bool HasEntries => Entries.Count > 0;
}
```

---

### FamilyMetadataExtractionResult

Результат извлечения метаданных из `.rfa`. MVP — только файловые метаданные (имя, размер, хеш).
Post-MVP — глубокое извлечение (категория, типы, параметры).

**Файл:** `FamilyMetadataExtractionResult.cs`

```csharp
public sealed record FamilyMetadataExtractionResult(
    string FileName,
    long FileSizeBytes,
    string Sha256,
    DateTimeOffset? LastWriteTimeUtc,
    string? CategoryName,
    int? RevitMajorVersion,
    IReadOnlyList<FamilyTypeDescriptor>? Types,
    IReadOnlyList<FamilyParameterDescriptor>? Parameters);
```

---

### FamilyTypeDescriptor

Дескриптор типоразмера семейства (Post-MVP: заполняется при глубоком извлечении).

**Файл:** `FamilyTypeDescriptor.cs`

```csharp
public sealed record FamilyTypeDescriptor(
    string Id,
    string CatalogItemId,
    string Name,
    int SortOrder,
    string? VersionId = null,
    string? FileId = null,
    string? ExtractionRunId = null);
```

---

### FamilyParameterDescriptor

Дескриптор параметра семейства (Post-MVP: заполняется при глубоком извлечении).

**Файл:** `FamilyParameterDescriptor.cs`

```csharp
public sealed record FamilyParameterDescriptor(
    string Id,
    string VersionId,
    string? TypeId,
    string Name,
    string? StorageType,
    string? ValueText,
    bool? IsInstance,
    bool? IsReadonly,
    string? ForgeTypeId);
```

---

### ProjectFamilyUsage

Запись истории использования семейства в проекте Revit.

**Файл:** `ProjectFamilyUsage.cs`

```csharp
public sealed record ProjectFamilyUsage(
    string Id,
    string CatalogItemId,
    string? VersionId,
    string? ProjectName,
    string? ProjectPath,
    int? RevitMajorVersion,
    string Action,
    DateTimeOffset CreatedAtUtc);
```

---

## RBAC Models *(FamilyManager)*

Модели системы Role-Based Access Control для локальных каталогов FamilyManager. Живут в `SmartCon.Core/Models/FamilyManager/`.

### DbUserRole

Роль пользователя в каталоге. Определяет уровень доступа.

**Файл:** `DbUserRole.cs`

```csharp
public enum DbUserRole
{
    Owner = 0,
    BimMaster = 1,
    Engineer = 2
}
```

---

### DbUserStatus

Статус пользователя: активен или заблокирован.

**Файл:** `DbUserStatus.cs`

```csharp
public enum DbUserStatus
{
    Active = 0,
    Banned = 1
}
```

---

### DbUser

Запись пользователя каталога с ролью и статусом.

**Файл:** `DbUser.cs`

```csharp
public sealed record DbUser(
    string UserId,
    string DisplayName,
    DbUserRole Role,
    DbUserStatus Status,
    DateTimeOffset JoinedAtUtc,
    DateTimeOffset LastSeenAtUtc
);
```

---

### UserIdentity

Идентификация текущего пользователя. Формат UserId: `"{UserName}@{MachineName}"`.

**Файл:** `UserIdentity.cs`

```csharp
public sealed record UserIdentity(
    string UserId,
    string DisplayName,
    string MachineName,
    string UserName
);
```

---

### DbAccessDeniedException

Исключение, выбрасываемое когда текущий пользователь заблокирован (Banned). Поддерживает NET48 serialization.

**Файл:** `DbAccessDeniedException.cs`

```csharp
public sealed class DbAccessDeniedException : Exception
{
    public string DbName { get; }
    public string OwnerDisplayName { get; }
}
```

---

### CategoryAnalysis *(System Families)*

Результат анализа одной системной категории в активном проекте Revit.
Содержит **только** типы, реально размещённые в модели (`WhereElementIsNotElementType`).
`BuiltInCategory` — value-type carrier (допустим в Core по I-09).

**Файл:** `CategoryAnalysis.cs`

```csharp
public sealed record CategoryAnalysis(
    BuiltInCategory Category,
    string DisplayName,
    IReadOnlyList<SystemTypeInfo> Types)
{
    public int TypeCount => Types.Count;
}

public sealed record SystemTypeInfo(
    string Name,
    string UniqueId);
```

---

### SelectedSystemType *(System Families)*

Иммутабельный снапшот одного выбранного пользователем типа системного семейства
(из picker flow в активном проекте). `UniqueId` используется для последующего
копирования через `ElementTransformUtils.CopyElements`. `Category` (BuiltInCategory)
нужен для выбора placement-handler'а в `SystemCategoryRegistry` и должен
соответствовать одному из значений в `SystemCategoryRegistry.SupportedCategories`.

**Файл:** `SelectedSystemType.cs`

```csharp
public sealed record SelectedSystemType(
    string UniqueId,
    string Name,
    string CategoryName,
    BuiltInCategory Category);
```

| Поле | Тип | Назначение |
|---|---|---|
| `UniqueId` | `string` | Стабильный идентификатор типа в source-документе (для `ElementTransformUtils.CopyElements`) |
| `Name` | `string` | Отображаемое имя типа (для UI и логов) |
| `CategoryName` | `string` | Локализованное имя категории ("Трубы", "Воздуховоды"). Используется в UI |
| `Category` | `BuiltInCategory` | Канонический enum-значение категории. **Может быть `BuiltInCategory.INVALID`** если категория — custom sub-category; staging копирует тип, но не размещает инстансы |

---

### CreateCleanProjectResult *(System Families)*

Результат создания чистого .rvt-проекта с копиями системных типов **и** инстансами
на сетке 2×2 м. `FilePath` — абсолютный путь к сохранённому .rvt во временной папке
(`%TEMP%\SmartCon\SystemFamilyLoadFromProject\<GUID>\<safeName>.rvt`, путь — из
`SystemFamilyTempLayout`).

**Файл:** `CreateCleanProjectResult.cs`

```csharp
public sealed record CreateCleanProjectResult(
    bool Success,
    string? FilePath,
    string? Error,
    int CopiedElementsCount,
    string? CategoryName = null,
    int PlacedInstancesCount = 0);
```

| Поле | Тип | Назначение |
|---|---|---|
| `Success` | `bool` | `true` если .rvt создан, сохранён и закрыт без ошибок |
| `FilePath` | `string?` | Абсолютный путь к staged .rvt (для передачи в extraction) или `null` при неудаче |
| `Error` | `string?` | Текст ошибки при `Success == false` |
| `CopiedElementsCount` | `int` | Сколько типов успешно скопировано (может быть меньше запрошенного из-за коллизий имён) |
| `CategoryName` | `string?` | Локализованное имя категории (для UI / логов) |
| `PlacedInstancesCount` | `int` | Сколько инстансов реально размещено. `0` если у категории нет placement handler'а в `SystemCategoryRegistry` (например, Floors, Roofs, Ceilings) или если тип не прошёл cast (FlexPipe → PipeType) |

---

### SystemFamilyBatchImportItem *(System Families)*

Элемент batch-диалога импорта системных семейств (один на категорию).
Содержит метаданные для проверки дублей и финального импорта в managed storage
(поле `TempRvtPath` — путь к временному .rvt, `TypeNames` — список имён типов).

**Файл:** `SystemFamilyBatchImportItem.cs`

```csharp
public sealed record SystemFamilyBatchImportItem(
    string Id,
    string SourceCategoryName,
    string FamilyName,
    string NormalizedName,
    IReadOnlyList<string> TypeNames,
    int TypeCount,
    FamilyBatchImportStatus Status,
    FamilyBatchImportAction Action,
    string? ExistingCatalogItemId = null,
    string? TempRvtPath = null,
    string? Sha256 = null,
    string? TargetCategoryId = null);
```

---

### SystemFamilyImportResult / SystemFamilyExtractionTask *(System Families)*

Результат финального импорта системных семейств из подготовленных .rvt в managed storage.
`ExtractionTasks` — по одному на каждую импортированную категорию (атрибуты
извлекаются отдельным фоновым сервисом `ISystemFamilyAttributeExtractionService`).

**Файл:** `SystemFamilyImportResult.cs`

```csharp
public sealed record SystemFamilyImportResult(
    bool Success,
    string? Message,
    IReadOnlyList<SystemFamilyExtractionTask> ExtractionTasks,
    int TypesCount);

public sealed record SystemFamilyExtractionTask(
    string CatalogItemId,
    string TempRvtPath,
    IReadOnlyList<string> TypeNames,
    string? VersionId,
    string? FileId);
```




---

## Cross-cutting Models (Phase 3)

### FamilyMetadataFormat

`SmartCon.Core/Models/FamilyManager/FamilyMetadataFormat.cs` — единственный источник правды для формата metadata package.

```csharp
public static class FamilyMetadataFormat
{
    public const string Id = "smartcon.family-metadata";
    public const int CurrentVersion = 2;

    public static bool IsRecognized(string format, int version)
        => format == Id && version <= CurrentVersion;
}
```

### FamilyMetadataMigrator

`SmartCon.Core/Models/FamilyManager/FamilyMetadataMigrator.cs` — stub v1→v2 migrator.

```csharp
public static class FamilyMetadataMigrator
{
    public static FamilyMetadataPackage Migrate(FamilyMetadataPackage package)
    {
        if (package.Format != FamilyMetadataFormat.Id)
            throw new NotSupportedException($"Unknown format: {package.Format}");
        if (package.Version > FamilyMetadataFormat.CurrentVersion)
            throw new NotSupportedException($"Version {package.Version} > current {FamilyMetadataFormat.CurrentVersion}");
        return package;
    }
}
```

### JsonOptions

`SmartCon.Core/Services/Json/JsonOptions.cs` — статические singleton-ы для JSON сериализации:
- `Default` — strict (no indented, no relaxed escaping)
- `WriteIndented` — pretty
- `RelaxedWriteIndented` — кириллица (Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

---

## DB Migration Models (Phase 4a)

### ILocalCatalogMigrator

DIP abstraction для мигратора локального каталога. Interface в Core, реализация `public sealed class LocalCatalogMigrator` в FamilyManager (ранее был internal class, повышена видимость для cross-assembly DI).

### LocalCatalogDatabase (повышена видимость)

`public sealed class LocalCatalogDatabase` (ранее `internal`). Содержит путь к файлу БД и factory для `SqliteConnection`. Повышение видимости — CS0051 fix (public ctor принимал internal параметр).

---

## FamilyMetadataPackageExtensions (Phase 1)

`SmartCon.Core/Models/FamilyManager/FamilyMetadataPackageExtensions.cs`:
```csharp
public static FamilyMetadataPackage WithNonNullCollections(this FamilyMetadataPackage package);
```

Гарантирует непустые коллекции (`Categories ?? []`, `Attributes ?? []`, `Bindings ?? []`) для безопасной JSON-сериализации. Использует `with` expression для immutable update.