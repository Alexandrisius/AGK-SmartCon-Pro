# Интерфейсы и контракты

> Загружать: при реализации или вызове сервисов.
> **Правило:** Интерфейсы объявлены в `SmartCon.Core/Services/Interfaces/`. Реализации — в `SmartCon.Revit/` или `SmartCon.Core/Services/Implementation/`.

---

## IRevitContext

Доступ к актуальному Document и UIDocument. Не кешировать — запрашивать при каждой операции.

**Файл:** `SmartCon.Core/Services/Interfaces/IRevitContext.cs`
**Реализация:** `SmartCon.Revit/Context/RevitContext.cs`

```csharp
public interface IRevitContext
{
    Document GetDocument();
    string GetRevitVersion();  // "2025", "2026"
    // UIDocument не экспонируется в Core (I-09). Доступен через RevitContext в Revit-слое.
}
```

---

## IRevitContextWriter

Обновление контекста Revit. Вызывается из `IExternalCommand.Execute()` и `IExternalEventHandler.Execute()` перед любой работой с `IRevitContext`. Отделён от `IRevitContext` по ISP (Interface Segregation Principle).

**Файл:** `SmartCon.Core/Services/Interfaces/IRevitContextWriter.cs`
**Реализация:** `SmartCon.Revit/Context/RevitContext.cs` (тот же класс, что и IRevitContext)

```csharp
public interface IRevitContextWriter
{
    // object вместо UIApplication — Core не зависит от RevitAPIUI (I-09). Передавать UIApplication.
    void SetContext(object revitUIApplication);
}
```

---

## ITransactionService

Все изменения модели — только через этот интерфейс. Создание `new Transaction(doc)` напрямую запрещено.

**Файл:** `SmartCon.Core/Services/Interfaces/ITransactionService.cs`
**Реализация:** `SmartCon.Revit/Transactions/RevitTransactionService.cs`

```csharp
public interface ITransactionService
{
    /// Запускает Transaction, выполняет action, делает Commit.
    /// При исключении — Rollback. Возвращает false при неудаче.
    bool RunInTransaction(string name, Action<Document> action);

    /// Запускает TransactionGroup. Используется для PipeConnect (одна запись Undo).
    bool RunInTransactionGroup(string name, Action<Document> action);
}
```

---

## IElementSelectionService

Интерактивный выбор элементов в Revit с кастомным фильтром.

**Файл:** `SmartCon.Core/Services/Interfaces/IElementSelectionService.cs`
**Реализация:** `SmartCon.Revit/Selection/ElementSelectionService.cs`

```csharp
public interface IElementSelectionService
{
    /// Выбор одного элемента со свободным коннектором.
    /// Фильтрация (ISelectionFilter) — деталь реализации Revit-слоя. Core не ссылается на RevitAPIUI (I-09).
    /// Возвращает (ElementId, XYZ clickPoint) или null при ESC/отмене.
    (ElementId ElementId, XYZ ClickPoint)? PickElementWithFreeConnector(string statusMessage);
}
```

---

## IFittingMapper

Поиск подходящих фитингов по типам коннекторов.

**Файл:** `SmartCon.Core/Services/Interfaces/IFittingMapper.cs`
**Реализация:** `SmartCon.Core/Services/Implementation/FittingMapper.cs`

```csharp
public interface IFittingMapper
{
    /// Упорядоченный по Priority список подходящих маппингов.
    IReadOnlyList<FittingMappingRule> GetMappings(
        ConnectionTypeCode from, ConnectionTypeCode to);

    /// Минимальная цепочка фитингов через промежуточные типы (алгоритм Дейкстры).
    /// Пустой список = соединение невозможно.
    IReadOnlyList<FittingMappingRule> FindShortestFittingPath(
        ConnectionTypeCode from, ConnectionTypeCode to);

    void LoadFromFile(string jsonPath);
}
```

---

## IFittingMappingRepository

CRUD для правил маппинга фитингов. Хранение — per-project: `DataStorage` в
`ExtensibleStorage` активного `Document` ([ADR-012](../adr/012-per-project-extensible-storage.md)).
Импорт/Экспорт в JSON выполняется вручную через окно Settings.

**Файл:** `SmartCon.Core/Services/Interfaces/IFittingMappingRepository.cs`
**Реализация:** `SmartCon.Revit/Storage/RevitFittingMappingRepository.cs`
**Сериализация:** `SmartCon.Core/Services/Storage/FittingMappingJsonSerializer.cs` (pure C#, unit-tested)

```csharp
public interface IFittingMappingRepository
{
    IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes();
    void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types);

    IReadOnlyList<FittingMappingRule> GetMappingRules();
    void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules);

    /// Возвращает диагностическое описание вида
    /// "ExtensibleStorage:{docTitle}@SchemaV{n}" (не путь к файлу).
    string GetStoragePath();
}
```

**Lifecycle:**
- Read — read-only, без транзакций. Нет Schema → возвращается
  `MappingPayload.Empty` (проект открывается с пустыми коллекциями).
- Write — через `ITransactionService.RunInTransaction("SmartCon: Save Mapping", ...)`.
  Первый Save создаёт `DataStorage`; последующие обновляют `Entity`.

---

## IFormulaSolver

Универсальный AST-парсер и решатель формул Revit (Phase 6 — implemented).
Поддерживает: if() с неограниченной вложенностью, and(), or(), not(), арифметику, сравнения, тригонометрию, log/ln/exp, size_lookup().
Комбинированный SolveFor: IF-упрощение → алгебраическая инверсия → бисекция.

**Файл:** `SmartCon.Core/Services/Interfaces/IFormulaSolver.cs`
**Реализация:** `SmartCon.Core/Math/FormulaEngine/Solver/FormulaSolver.cs`
**DI:** `ServiceRegistrar.cs` → `IFormulaSolver → FormulaSolver` (Singleton)
**Заменяет:** ~~`MiniFormulaSolver.cs`~~ (удалён)

```csharp
public interface IFormulaSolver
{
    /// Прямое вычисление формулы при заданных параметрах (Internal Units).
    double Evaluate(string formula, IReadOnlyDictionary<string, double> parameterValues);

    /// Обратное решение: найти значение variableName при котором formula = targetValue.
    double SolveFor(string formula, string variableName, double targetValue,
                    IReadOnlyDictionary<string, double> otherValues);

    /// Парсинг size_lookup(...) — извлечение имени таблицы и порядка параметров.
    (string TableName, IReadOnlyList<string> ParameterOrder) ParseSizeLookup(string formula);
}
```

---

## IElementChainIterator

Обход цепочек соединённых MEP-элементов.

**Файл:** `SmartCon.Core/Services/Interfaces/IElementChainIterator.cs`
**Реализация:** `SmartCon.Revit/Selection/ElementChainIterator.cs`

```csharp
public interface IElementChainIterator
{
    /// Строит ConnectionGraph начиная с элемента. BFS через AllRefs.
    ConnectionGraph BuildGraph(Document doc, ElementId startElementId,
        IReadOnlySet<ElementId>? stopAtElements = null);

    /// Свободные коннекторы на границах цепочки.
    IReadOnlyList<ConnectorProxy> GetChainEndConnectors(
        Document doc, ConnectionGraph graph);
}
```

---

## IDialogService

Абстракция для открытия окон из ViewModel (MVVM).

**Файл:** `SmartCon.Core/Services/Interfaces/IDialogService.cs`
**Реализация:** `SmartCon.PipeConnect/Services/PipeConnectDialogService.cs`

```csharp
public interface IDialogService
{
    /// Открыть MiniTypeSelector рядом с курсором. Возвращает выбранный тип или null.
    ConnectorTypeDefinition? ShowMiniTypeSelector(
        IReadOnlyList<ConnectorTypeDefinition> availableTypes);

    /// Показать предупреждение пользователю.
    void ShowWarning(string title, string message);

    /// Открыть окно выбора семейств фитингов для правила маппинга.
    /// Возвращает упорядоченный по приоритету список или null при отмене.
    IReadOnlyList<FittingMapping>? ShowFamilySelector(
        IReadOnlyList<string> availableFamilies,
        IReadOnlyList<FittingMapping> currentSelection);
}
```

---

## ITransformService *(Phase 2)*

Перемещение и поворот элементов. Работает с Vec3 — конвертация Vec3↔XYZ внутри реализации.
Все вызовы должны быть внутри открытой Transaction.

**Файл:** `SmartCon.Core/Services/Interfaces/ITransformService.cs`
**Реализация:** `SmartCon.Revit/Transform/RevitTransformService.cs`

```csharp
public interface ITransformService
{
    void MoveElement(Document doc, ElementId elementId, Vec3 offset);
    void RotateElement(Document doc, ElementId elementId,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians);
    void MoveElements(Document doc, ICollection<ElementId> elementIds, Vec3 offset);
    void RotateElements(Document doc, ICollection<ElementId> elementIds,
        Vec3 axisOrigin, Vec3 axisDirection, double angleRadians);
}
```

---

## IConnectorService *(Phase 2)*

Работа с коннекторами: ближайший к точке клика, обновление после трансформации, ConnectTo.

**Файл:** `SmartCon.Core/Services/Interfaces/IConnectorService.cs`
**Реализация:** `SmartCon.Revit/Selection/ConnectorService.cs`

```csharp
public interface IConnectorService
{
    /// Ближайший свободный коннектор к точке клика. null если нет свободных.
    ConnectorProxy? GetNearestFreeConnector(Document doc, ElementId elementId, XYZ clickPoint);

    /// Перечитать коннектор после трансформации (I-05: не кешировать).
    ConnectorProxy? RefreshConnector(Document doc, ElementId elementId, int connectorIndex);

    /// ConnectTo. Возвращает true при успехе.
    bool ConnectTo(Document doc,
        ElementId elementId1, int connectorIndex1,
        ElementId elementId2, int connectorIndex2);
}
```

---

## IFamilyConnectorService *(Phase 3)*

Запись ConnectionTypeCode в Description коннектора семейства через EditFamily API.

**Файл:** `SmartCon.Core/Services/Interfaces/IFamilyConnectorService.cs`
**Реализация:** `SmartCon.Revit/Family/RevitFamilyConnectorService.cs`

```csharp
public interface IFamilyConnectorService
{
    /// Записать ConnectionTypeCode в Description коннектора семейства.
    /// Использует EditFamily API. Вызывать внутри Transaction (I-03).
    bool SetConnectorTypeCode(Document doc, ElementId elementId,
                              int connectorIndex, ConnectionTypeCode typeCode);
}
```

---

## IFittingFamilyRepository *(Phase 3C)*

Получение семейств фитингов из проекта, подходящих для PipeConnect-маппинга.
Критерии: `OST_PipeFitting` + `PartType=MultiPort` + ровно 2 `ConnectorElement`.

**Файл:** `SmartCon.Core/Services/Interfaces/IFittingFamilyRepository.cs`
**Реализация:** `SmartCon.Revit/Family/FittingFamilyRepository.cs`

```csharp
public interface IFittingFamilyRepository
{
    /// Вызывать ВНЕ транзакции (EditFamily требует doc.IsModifiable == false).
    IReadOnlyList<FamilyInfo> GetEligibleFittingFamilies(Document doc);
}
```

**Алгоритм:**
1. `FilteredElementCollector` → все `Family` с `FamilyCategory = OST_PipeFitting`
2. Фильтр `FAMILY_PART_TYPE == PartType.MultiPort` (без EditFamily)
3. `doc.EditFamily(family)` → считать `OST_ConnectorElem` → оставить только с count == 2
4. Вернуть `FamilyInfo` с именем, PartTypeName, ConnectorCount, SymbolNames

---

## IParameterResolver *(Phase 4)*

Анализ зависимостей параметров коннектора семейства и запись нового радиуса.

**Файл:** `SmartCon.Core/Services/Interfaces/IParameterResolver.cs`
**Реализация:** `SmartCon.Revit/Parameters/RevitParameterResolver.cs`

```csharp
public interface IParameterResolver
{
    /// Вернуть цепочку зависимостей параметра CONNECTOR_RADIUS.
    /// Вызывать ВНЕ транзакции (требует EditFamily).
    IReadOnlyList<ParameterDependency> GetConnectorRadiusDependencies(
        Document doc, ElementId elementId, int connectorIndex);

    /// Установить радиус коннектора (через параметр семейства или ChangeTypeId).
    /// Вызывать ВНУТРИ транзакции.
    bool TrySetConnectorRadius(
        Document doc, ElementId elementId, int connectorIndex, double targetRadius);

    /// Подбирает типоразмер фитинга-переходника:
    /// 1. staticConnIdx должен точно совпасть с staticRadius.
    /// 2. Среди подходящих типов — dynConnIdx максимально близок к dynRadius.
    /// Возвращает (StaticExact, AchievedDynRadius) — фактический радиус dynConn после смены типа.
    /// Вызывать ВНУТРИ транзакции.
    (bool StaticExact, double AchievedDynRadius) TrySetFittingTypeForPair(
        Document doc, ElementId fittingId,
        int staticConnIdx, double staticRadius,
        int dynConnIdx,    double dynRadius);
}
```

---

## ILookupTableService *(Phase 4)*

Работа с LookupTable (size_lookup) семейства Revit.

**Файл:** `SmartCon.Core/Services/Interfaces/ILookupTableService.cs`
**Реализация:** `SmartCon.Revit/Parameters/RevitLookupTableService.cs`

```csharp
public interface ILookupTableService
{
    /// Есть ли LookupTable у коннектора.
    bool HasLookupTable(Document doc, ElementId elementId, int connectorIndex);

    /// Доступен ли точный радиус в таблице.
    bool ConnectorRadiusExistsInTable(
        Document doc, ElementId elementId, int connectorIndex, double radiusFt);

    /// Найти ближайший доступный радиус из таблицы.
    double GetNearestAvailableRadius(
        Document doc, ElementId elementId, int connectorIndex, double targetRadiusFt);
}
```

---

## IRevitUIContext *(Phase 2, Revit layer only)*

Доступ к UIDocument и UIApplication. **НЕ экспонируется в Core** (I-09).
Используется ElementSelectionService для PickObject.

**Файл:** `SmartCon.Revit/Context/IRevitUIContext.cs`
**Реализация:** `SmartCon.Revit/Context/RevitContext.cs`

```csharp
// Только в Revit-слое — не Core!
public interface IRevitUIContext
{
    UIDocument GetUIDocument();
    UIApplication GetUIApplication();
}
```

---

## ITransactionGroupSession

Сессия TransactionGroup для PipeConnect (одна запись Undo).

**Файл:** `SmartCon.Core/Services/Interfaces/ITransactionGroupSession.cs`
**Реализация:** `SmartCon.Revit/Transactions/RevitTransactionService.cs`

```csharp
public interface ITransactionGroupSession
{
    bool RunInTransaction(string name, Action<Document> action);
    void Assimilate();
    void RollBack();
}
```

---

## IFittingInsertService

Вставка, удаление и позиционирование фитингов.

**Файл:** `SmartCon.Core/Services/Interfaces/IFittingInsertService.cs`
**Реализация:** `SmartCon.Revit/Fittings/RevitFittingInsertService.cs`

```csharp
public interface IFittingInsertService
{
    ElementId? InsertFitting(Document doc, string familyName, string symbolName, XYZ origin);
    ConnectorProxy? AlignFittingToStatic(Document doc, ElementId fittingId, ConnectorProxy staticConn,
        ITransformService transformSvc, IConnectorService connSvc,
        ConnectionTypeCode? dynamicTypeCode = null,
        IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null,
        IReadOnlyList<FittingMappingRule>? directConnectRules = null);
    void DeleteElement(Document doc, ElementId elementId);
}
```

---

## IDynamicSizeResolver

Разрешение типоразмеров динамических семейств.

**Файл:** `SmartCon.Core/Services/Interfaces/IDynamicSizeResolver.cs`
**Реализация:** `SmartCon.Revit/Parameters/RevitDynamicSizeResolver.cs`

```csharp
public interface IDynamicSizeResolver
{
    IReadOnlyList<FamilySizeOption> GetAvailableSizes(Document doc, ElementId elementId);
}
```

---

## INetworkMover

Перемещение подключённой сети элементов и вставка переходников.

**Файл:** `SmartCon.Core/Services/Interfaces/INetworkMover.cs`
**Реализация:** `SmartCon.Revit/Network/NetworkMover.cs`

```csharp
public interface INetworkMover
{
    void MoveNetwork(Document doc, ICollection<ElementId> elementIds, Vec3 offset);
    ElementId? InsertReducer(Document doc, ConnectorProxy staticConn, ConnectorProxy dynamicConn,
        IReadOnlyList<FittingMappingRule>? directConnectRules = null);
}
```

---

## IUpdateService / IUpdateSettingsRepository

GitHub-based auto-update система.

**Файл:** `SmartCon.Core/Services/Interfaces/IUpdateService.cs`, `IUpdateSettingsRepository.cs`
**Реализация:** `SmartCon.Revit/Updates/GitHubUpdateService.cs`, `SmartCon.Core/Services/Implementation/JsonUpdateSettingsRepository.cs`

```csharp
public interface IUpdateService
{
    string GetCurrentVersion();
    Task<UpdateInfo?> CheckForUpdateAsync();
    Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null);
    Task StageUpdateAsync(string zipPath);
    Task<UpdateInfo?> GetPendingUpdateAsync();
}

public interface IUpdateSettingsRepository
{
    string SettingsFilePath { get; }
    UpdateSettings Load();
    void Save(UpdateSettings settings);
}
```

---

## IFittingChainResolver

Единая точка принятия решений о цепочке фитингов/редьюсеров. Заменяет разрозненную логику из 5+ файлов.

**Файл:** `SmartCon.Core/Services/Interfaces/IFittingChainResolver.cs`
**Реализация:** `SmartCon.Core/Services/Implementation/FittingChainResolver.cs`
**DI:** `ServiceRegistrar.cs` → `IFittingChainResolver → FittingChainResolver` (Singleton)
**ADR:** [010-fitting-chain-resolver.md](../adr/010-fitting-chain-resolver.md)

```csharp
public interface IFittingChainResolver
{
    FittingChainPlan Resolve(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius);

    IReadOnlyList<FittingChainPlan> ResolveAlternatives(
        ConnectionTypeCode staticCtc, ConnectionTypeCode dynamicCtc,
        double staticRadius, double dynamicRadius,
        int maxAlternatives = 3);
}
```

---

## IShareProjectSettingsRepository *(ProjectManagement)*

CRUD для настроек модуля ShareProject. Хранение — per-project: `DataStorage` в
`ExtensibleStorage` активного `Document` (ADR-013).

**Файл:** `SmartCon.Core/Services/Interfaces/IShareProjectSettingsRepository.cs`
**Реализация:** `SmartCon.Revit/Storage/RevitShareProjectSettingsRepository.cs`
**Сериализация:** `SmartCon.Core/Services/Storage/ShareSettingsJsonSerializer.cs` (pure C#)

```csharp
public interface IShareProjectSettingsRepository
{
    ShareProjectSettings Load();
    void Save(ShareProjectSettings settings);
    string ExportToJson(ShareProjectSettings settings);
    ShareProjectSettings ImportFromJson(string json);
}
```

---

## IShareProjectService *(ProjectManagement)*

Основная операция Share: sync → detach → purge → saveAs to Shared folder.

**Файл:** `SmartCon.Core/Services/Interfaces/IShareProjectService.cs`
**Реализация:** `SmartCon.Revit/Sharing/RevitShareProjectService.cs`

```csharp
public interface IShareProjectService
{
    ShareProjectResult Share(ShareProjectSettings settings);
}
```

---

## IModelPurgeService *(ProjectManagement)*

Очистка модели: удаление элементов по категориям + purge неиспользуемых.

**Файл:** `SmartCon.Core/Services/Interfaces/IModelPurgeService.cs`
**Реализация:** `SmartCon.Revit/Sharing/RevitModelPurgeService.cs`

```csharp
public interface IModelPurgeService
{
    int Purge(Document doc, PurgeOptions options, List<string> keepViewNames);
}
```

---

## IFileNameParser *(ProjectManagement)*

Парсинг имени файла по шаблону, трансформация статуса, валидация.

**Файл:** `SmartCon.Core/Services/Interfaces/IFileNameParser.cs`
**Реализация:** `SmartCon.Revit/Sharing/RevitFileNameParser.cs`

```csharp
public interface IFileNameParser
{
    string? TransformStatus(string fileName, FileNameTemplate template);
    bool Validate(string fileName, FileNameTemplate template, out string error);
    Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template);
}
```

---

## IViewRepository *(ProjectManagement)*

Получение списка видов из Revit документа для отображения в UI.

**Файл:** `SmartCon.Core/Services/Interfaces/IViewRepository.cs`
**Реализация:** `SmartCon.Revit/Sharing/RevitViewRepository.cs`

```csharp
public interface IViewRepository
{
    List<ViewInfo> GetAllViews(Document doc);
}
```
