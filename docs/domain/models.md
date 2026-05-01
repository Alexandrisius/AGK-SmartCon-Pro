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

## FamilyManager Models

Модели модуля FamilyManager (Phase 13: Published Storage). Все модели — immutable records, живут в `SmartCon.Core/Models/FamilyManager/`.
Идентификаторы — `string` (GUID), даты — `DateTimeOffset`.

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
    int RevitMajorVersion);
```

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
    string? Description);
```

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
    string? Description);
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

### FamilyResolvedFile

Разрешённый путь к файлу `.rfa` — готов для загрузки в Revit.

**Файл:** `FamilyResolvedFile.cs`

```csharp
public sealed record FamilyResolvedFile(
    string AbsolutePath,
    string? CatalogItemId,
    string? VersionId);
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
    int SortOrder);
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


