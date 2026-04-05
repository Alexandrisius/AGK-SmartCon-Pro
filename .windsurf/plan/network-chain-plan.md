# План: Перемещение цепочки элементов сети (Network Chain Movement)

**Статус:** Draft v2 (полная переработка)  
**Дата:** 2025-07-13  
**Контекст:** SmartCon — плагин для Revit 2025, .NET 8 / C# 12 / WPF, MEP-соединения, PipeConnect  
**Фаза roadmap:** Phase 7 — Цепочки (Chain)

> **Для агента-исполнителя:** Этот документ — исчерпывающая спецификация.
> Перед началом работы загрузи `docs/invariants.md` и `docs/architecture/dependency-rule.md`.
> Все ссылки на существующие файлы — абсолютные от корня `src/`.

---

## 1. Проблема

При перемещении/вращении динамического элемента пользователь хочет, чтобы часть присоединённой к нему сети двигалась вместе с ним. Текущий чекбокс `MoveEntireChain` (bool) в `PipeConnectEditorViewModel` не реализован. Нужен **пошаговый контроль**: пользователь сам решает, сколько уровней элементов из цепочки перемещать, с возможностью отката по одному уровню.

### 1.1. Что уже есть (инвентаризация существующего кода)

| Компонент | Файл | Статус |
|-----------|------|--------|
| `ConnectionGraph` | `SmartCon.Core/Models/ConnectionGraph.cs` | ✅ Есть: Nodes, Edges, RootId, AddNode, AddEdge, GetChainFrom |
| `ConnectionEdge` | `SmartCon.Core/Models/ConnectionEdge.cs` | ✅ Есть: FromElementId, FromConnectorIndex, ToElementId, ToConnectorIndex |
| `IElementChainIterator` | `SmartCon.Core/Services/Interfaces/IElementChainIterator.cs` | ✅ Интерфейс есть: BuildGraph, GetChainEndConnectors |
| `ElementChainIterator` реализация | `SmartCon.Revit/Selection/ElementChainIterator.cs` | ❌ НЕ существует — нужно создать |
| `IConnectorService` | `SmartCon.Core/Services/Interfaces/IConnectorService.cs` | ✅ Есть: GetNearestFreeConnector, RefreshConnector, ConnectTo, DisconnectAllFromConnector, GetAllFreeConnectors, GetAllConnectors |
| `ITransformService` | `SmartCon.Core/Services/Interfaces/ITransformService.cs` | ✅ Есть: MoveElement, RotateElement, RotateElements |
| `ITransactionGroupSession` | `SmartCon.Core/Services/Interfaces/ITransactionGroupSession.cs` | ✅ Есть: RunInTransaction, Assimilate, RollBack |
| `IParameterResolver` | `SmartCon.Core/Services/Interfaces/IParameterResolver.cs` | ✅ Есть: TrySetConnectorRadius, GetConnectorRadiusDependencies, TrySetFittingTypeForPair |
| `IFittingInsertService` | `SmartCon.Core/Services/Interfaces/IFittingInsertService.cs` | ✅ Есть: InsertFitting, AlignFittingToStatic, DeleteElement |
| `ConnectorAligner` | `SmartCon.Core/Math/ConnectorAligner.cs` | ✅ Есть: ComputeAlignment (чистая математика) |
| `PipeConnectEditorViewModel` | `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` | ✅ Есть, ~1256 строк — модальное окно (ShowDialog) |
| `PipeConnectCommand` | `SmartCon.PipeConnect/Commands/PipeConnectCommand.cs` | ✅ Есть, 344 строки — точка входа |
| `PipeConnectionSession` | `SmartCon.Core/Models/PipeConnectionSession.cs` | ⚠️ Есть, содержит `MoveEntireChain` — будет удалён |
| `ServiceRegistrar` | `SmartCon.App/DI/ServiceRegistrar.cs` | ✅ Есть — место регистрации DI |

### 1.2. Что нужно создать / изменить

| Действие | Компонент | Описание |
|----------|-----------|----------|
| **Создать** | `ElementChainIterator` | Реализация `IElementChainIterator` — BFS по AllRefs |
| **Создать** | `NetworkSnapshot` | Снимок состояния элементов для отката |
| **Создать** | `INetworkMover` | Сервис перемещения уровня цепочки к родителю |
| **Изменить** | `ConnectionGraph` | Добавить `Levels`, `SavedConnections` |
| **Изменить** | `PipeConnectEditorViewModel` | Заменить `MoveEntireChain` на `+`/`−` |
| **Изменить** | `PipeConnectEditorView.xaml` | Заменить чекбокс на UI глубины |
| **Изменить** | `PipeConnectCommand` | Построение графа перед открытием окна |
| **Изменить** | `PipeConnectSessionContext` | Добавить `ChainGraph` |
| **Удалить** | `PipeConnectionSession.MoveEntireChain` | Устаревший bool |

---

## 2. Бизнес-кейсы

### 2.1. Терминология

| Термин | Определение |
|--------|-------------|
| **Статический элемент (Static)** | Точка отсчёта. Остаётся на месте. Не двигается. Выбирается вторым кликом (S2). |
| **Динамический элемент (Dynamic)** | Элемент, который перемещается/вращается. Выбирается первым кликом (S1). |
| **Цепочка (Chain)** | Все элементы сети, соединённые с Dynamic, кроме Static. |
| **Уровень (Level)** | Группа элементов на одинаковом BFS-расстоянии от Dynamic. Level 0 = Dynamic. |
| **Глубина (Depth)** | Текущий выбранный уровень. 0 = только Dynamic, 1 = Dynamic + Level 1, и т.д. |
| **Snapshot** | Сохранённое состояние элементов уровня (позиция, размер, соединения) для отката. |
| **Reducer** | Переходник, вставляемый когда DN соседних элементов не совпадает. |

### 2.2. Сценарий: Только динамический (Depth = 0)

**Начало:** Пользователь выбрал Static + Dynamic, запустил плагин.

**Что происходит (под капотом):**
1. `PipeConnectCommand` строит `ConnectionGraph` через `IElementChainIterator.BuildGraph()` — **ДО** disconnect
2. Dynamic отсоединён от всех коннекторов (`DisconnectAllFromConnector` для каждого коннектора)
3. Dynamic выровнен к Static (существующий алгоритм S3)
4. WPF-окно: `Сеть: [−] 0 [+]` + label "N элементов в цепочке"

**Действия:** Пользователь настраивает угол, подбирает фитинг, меняет размер. Счётчик = 0.

**Rotate:** Вращается только Dynamic + текущий fitting (существующее поведение).

**Connect (Соединить):** Соединяется Static ↔ fitting ↔ Dynamic. Элементы цепочки **НЕ затрагиваются** — они остались на исходных местах, отсоединённые от Dynamic.

**Итог:** Dynamic перемещён/повёрнут. Остальная сеть осталась на своих исходных местах.

---

### 2.3. Сценарий: Пошаговое присоединение (+)

**Начало:** Цепочка: Dynamic → Тройник → [Труба1, Труба2] → [Отвод1, Отвод2, Отвод3, Отвод4]

**Уровни BFS:**
```
Уровень 0: [Dynamic]
Уровень 1: [Тройник]
Уровень 2: [Труба1, Труба2]
Уровень 3: [Отвод1, Отвод2, Отвод3, Отвод4]
```

**Клик + (первый раз):**
- Присоединяются элементы Уровня 1: Тройник (1 элемент)
- Транзакция "Цепочка: уровень 1"
- Тройник отсоединяется от Труба1 и Труба2 (DisconnectAll)
- Тройник присоединяется к Dynamic (ConnectTo)
- Размер тройника подбирается под DN dynamic (ChangeTypeId или reducer)
- Трубы НЕ меняют размер — при несовпадении DN вставляется reducer
- Счётчик = 1: `[−] 1 [+]  1/7 элементов в 1/3 ур.`

**Клик + (второй раз):**
- Присоединяются элементы Уровня 2: Труба1, Труба2 (2 элемента)
- Транзакция "Цепочка: уровень 2"
- Каждая труба отсоединяется от своих отводов (DisconnectAll)
- Каждая труба присоединяется к тройнику (ConnectTo)
- Размеры труб НЕ меняются — при несовпадении DN вставляются reducers
- Счётчик = 2: `[−] 2 [+]  3/7 элементов в 2/3 ур.`

**Клик + (третий раз):**
- Присоединяются элементы Уровня 3: 4 отвода
- Счётчик = 3 = максимум. Кнопка + неактивна.
- Счётчик = 3: `[−] 3 [+]  7/7 элементов в 3/3 ур.`

---

### 2.4. Сценарий: Откат (−)

**Начало:** Счётчик = 3 (все 7 элементов присоединены).

**Клик − (первый раз):**
- Отсоединяются элементы Уровня 3: 4 отвода
- Каждый отвод возвращается к исходной позиции, DN, углу, соединениям (из snapshot)
- Если был вставлен reducer на Уровне 3 — удаляется
- Счётчик = 2: `[−] 2 [+]  3/7 элементов в 2/3 ур.`

**Клик − (второй раз):**
- Отсоединяются Труба1, Труба2
- Восстанавливаются из snapshot (позиция, DN, угол, соединения)
- Счётчик = 1.

**Клик − (третий раз):**
- Отсоединяется Тройник
- Восстанавливается из snapshot
- Счётчик = 0. Кнопка − неактивна.

**Важно:** Snapshot каждого элемента сохраняется **ДО операции +** (ДО DisconnectAll, ДО перемещения). Кнопка − восстанавливает из этого snapshot — элемент возвращается в **исходное положение в модели** (до присоединения к цепочке). Если между + и − были Rotate — вращения **теряются** для этого элемента при откате, что является корректным поведением.

---

### 2.5. Сценарий: Вращение с частичной сетью

**Начало:** Счётчик = 2 (присоединены: тройник + 2 трубы = 3 элемента).

**Rotate:**
- Вращаются: Dynamic + Fitting + Тройник + Труба1 + Труба2
- НЕ вращаются: Отвод1-4 (Уровень 3 — не присоединены)
- Ось вращения: StaticConnector.Origin + StaticConnector.BasisZ
- Все элементы вращаются как группа через `TransformElements`
- После вращения: коррекция углов (округление до кратного 15° относительно глобальной оси Y)

**Connect:**
- Соединяется static ↔ dynamic
- Подбираются фитинги для свободных коннекторов
- Труба1 и Труба2 уже соединены с тройником (через +)
- Свободные коннекторы труб — подбираются фитинги для соединения с отводами Уровня 3

---

### 2.6. Сценарий: Отмена (Cancel)

**Начало:** Пользователь нажал + три раза, повернул dynamic, соединил.

**Cancel:**
- `_groupSession.RollBack()` — откатывается вся TransactionGroup
- Все изменения отменяются: позиции, DN, соединения, фитинги, вращения
- Revit возвращается в исходное состояние

---

### 2.7. Сценарий: Цепочка с несколькими тройниками

**Конфигурация:**
```
Static → Dynamic → Тройник_A
                        ├── Труба_1 → Отвод_1
                        └── Тройник_B
                                    ├── Труба_2 → Отвод_2
                                    └── Труба_3 → Отвод_3
```

**Уровни BFS:**
```
Уровень 0: [Dynamic]
Уровень 1: [Тройник_A]
Уровень 2: [Труба_1, Тройник_B]
Уровень 3: [Отвод_1, Труба_2, Труба_3]
Уровень 4: [Отвод_2, Отвод_3]
```

**Пошаговое присоединение:**
| Клик | Счётчик | Элементы | Всего присоединено |
|------|---------|----------|-------------------|
| init | 0 | — | 0 |
| + | 1 | Тройник_A | 1 |
| + | 2 | Труба_1, Тройник_B | 3 |
| + | 3 | Отвод_1, Труба_2, Труба_3 | 6 |
| + | 4 | Отвод_2, Отвод_3 | 8 |

Каждый + присоединяет **все элементы одного уровня** одной транзакцией. Ветки наращиваются параллельно.

---

## 3. Архитектурные решения

### 3.1. Направление обхода

- **BFS от dynamic во все стороны**, кроме направления к static
- Коннектор dynamic который был соединён со static — **блокируется** (не начинается BFS с него)
- Static-элемент добавлен в `stopAtElements`

### 3.2. Когда строить граф

- **ДО disconnect** в `PipeConnectCommand`
- Коннекторы ещё соединены — `AllRefs` возвращает корректных соседей
- Для каждого элемента сохраняются исходные соединения (ConnectionRecord)

### 3.3. Межуровневой разрыв

- **НЕ разрываем** соединения между уровнями при Init
- При +: для каждого элемента уровня N — `DisconnectAll` (это автоматически отсоединяет от уровня N+1) → затем `ConnectTo` с уровнем N-1
- Это значит что + делает "разрыв сверху, соединение снизу"

### 3.4. Snapshot для кнопки −

- **ДО каждой операции +** (ДО DisconnectAll, ДО перемещения) сохраняется snapshot каждого элемента уровня
- Snapshot содержит: позицию, DN, Transform (Origin+BasisX/Y/Z), FamilySymbolId, соединения с соседями
- При −: элементы уровня N восстанавливаются из snapshot → возвращаются в **исходное положение в модели** (до присоединения к цепочке)
- Если между + и − были Rotate/Resize — вращения/размеры **теряются** для этих элементов при откате (корректное поведение)

### 3.5. Подбор размеров (ИСПРАВЛЕНО v2.4)

> **КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ vs v2.3:** Три ошибки в предыдущей версии:
> 1. MEPCurve ошибочно пропускал `TrySetConnectorRadius` → шёл сразу в reducer.
>    Факт: `TrySetConnectorRadius` для MEPCurve **УЖЕ пишет** `RBS_PIPE_DIAMETER_PARAM` (строки 237-250).
> 2. Целевой радиус брался из `parentProxy.Radius` → каскадный сбой при неудаче Level 1.
> 3. Подгонялся только один коннектор → многопортовые фитинги (тройники) оставались "переходными".

**Единый алгоритм для ВСЕХ типов элементов (FamilyInstance, MEPCurve, FlexPipe):**

1. `targetRadius = _ctx.DynamicConnector.Radius` — **ВСЕГДА** радиус Dynamic-коннектора
   (вся цепочка подгоняется под Dynamic, а не каскадно parent→child)
2. Для КАЖДОГО элемента: `TrySetConnectorRadius(doc, elemId, connIdx, targetRadius)`
   — Работает для **всех** типов: MEPCurve пишет `RBS_PIPE_DIAMETER_PARAM`, FamilyInstance — параметр/тип
3. После `TrySetConnectorRadius` + `doc.Regenerate()` → **ВЕРИФИКАЦИЯ**:
   `RefreshConnector` → сравнить фактический радиус с `targetRadius`
   — Если `|actual - target| > 1e-5`: подгонка НЕ удалась → InsertReducer
   — Если совпадает: OK, reducer не нужен
4. Для **многопортовых фитингов** (тройники): подгонять ВСЕ коннекторы элемента,
   участвующие в графе цепочки (не только parent-facing)

**ВАЖНО:** `TrySetConnectorRadius` может вернуть `true`, но Revit округлит/ограничит значение.
Поэтому `return true` **НЕ является гарантией** — нужна верификация через `RefreshConnector`.

### 3.6. Определение DN элемента

- **DN = номинальный диаметр** — определяется через **привязку параметра к размеру коннектора**
- Для каждого коннектора: `Connector.Radius` связан с `FamilyParameter` через `MEPFamilyConnectorInfo.GetAssociateFamilyParameterId`
- Находим `FamilyParameter` который управляет `CONNECTOR_RADIUS` или `CONNECTOR_DIAMETER` → значение этого параметра = DN
- **НЕ используем хардкод** имён параметров ("DN", "Диаметр", "Size" и т.д.)
- DN определяется динамически: какой параметр привязан к размеру коннектора — тот и есть DN

**Используем существующий `IParameterResolver`:**
- `RevitParameterResolver.GetConnectorRadiusDependencies()` — уже определяет привязку параметра к коннектору
- Алгоритм: `connector.GetMEPConnectorInfo()` → `MEPFamilyConnectorInfo.GetAssociateFamilyParameterId(BuiltInParameter.CONNECTOR_RADIUS)` → получаем `FamilyParameter` → его значение = DN
- Для MEPCurve/FlexPipe: `RBS_PIPE_DIAMETER_PARAM` — прямой built-in параметр
- Для FamilyInstance: через `MEPFamilyConnectorInfo` + `FamilyManager` (если нужна формула)

**Получить текущий DN элемента:**
1. `IParameterResolver.GetConnectorRadiusDependencies(doc, elementId, connectorIndex)` → `ParameterDependency`
2. Если `dep.IsInstance && dep.DirectParamName != null`: `element.LookupParameter(dep.DirectParamName).AsDouble()` → DN (или DN/2 если `dep.IsDiameter`)
3. Если `dep.BuiltIn != null` (MEPCurve): `element.get_Parameter(dep.BuiltIn).AsDouble()` → DN
4. Fallback: `Connector.Radius * 2`

### 3.7. Коррекция углов при вращении

- При вращении накапливаются микро-ошибки угла
- После `RotateElements`: GlobalYSnap применяется **ТОЛЬКО к dynamic** — округление BasisY до ближайшего кратного 15° относительно глобальной оси Y
- Элементы цепочки **НЕ** снэпятся — они вращаются как rigid body вместе с dynamic
- Используется `ConnectorAligner.ComputeGlobalYAlignmentSnap`
- **ВАЖНО:** В текущем `ExecuteRotate` (строки 395-450) GlobalYSnap **отсутствует** — он есть только в `Init()` (строки 261-296). Нужно **ДОБАВИТЬ** GlobalYSnap в `ExecuteRotate` для dynamic элемента (по аналогии с Init)

### 3.8. Транзакции

| Операция | Транзакция |
|----------|-----------|
| Init (disconnect dynamic) | "Отсоединение dynamic" |
| + Уровень N | "Цепочка: уровень N" (DisconnectAll + ConnectTo + AdjustSize) |
| Rotate | "Поворот цепочки" |
| Connect | "Соединение" (может быть несколько транзакций) |
| − Уровень N | "Цепочка: откат уровня N" (DisconnectAll + Restore + Delete reducers) |
| Cancel | RollBack всей TransactionGroup |

---

## 4. Структура данных

### 4.1. ConnectionGraph (расширение существующего)

**Файл:** `SmartCon.Core/Models/ConnectionGraph.cs` (уже существует — расширить)

**Уже есть:** `RootId`, `Nodes` (IReadOnlyList), `Edges`, `AddNode`, `AddEdge`, `GetChainFrom`, `ElementIdEqualityComparer` (сейчас `internal` — **сделать `public`** или вынести в отдельный файл, см. ниже)

**Добавить:**
```csharp
// ═══ НОВЫЕ поля ═══
private readonly List<List<ElementId>> _levels = [];
public IReadOnlyList<IReadOnlyList<ElementId>> Levels => _levels;

private readonly Dictionary<long, List<ConnectionRecord>> _originalConnections = new();

// ═══ НОВЫЕ internal-методы (вызывает ElementChainIterator) ═══
internal void AddElementAtLevel(int level, ElementId elementId)
{
    while (_levels.Count <= level) _levels.Add([]);
    _levels[level].Add(elementId);
}

internal void SaveConnection(ElementId elementId, ConnectionRecord record)
{
    var key = elementId.Value;
    if (!_originalConnections.TryGetValue(key, out var list))
    { list = []; _originalConnections[key] = list; }
    if (!list.Any(r => r.ThisConnectorIndex == record.ThisConnectorIndex
                    && r.NeighborElementId.Value == record.NeighborElementId.Value))
        list.Add(record);
}

// ═══ НОВЫЕ public-методы ═══
public IReadOnlyList<ConnectionRecord> GetOriginalConnections(ElementId elementId)
    => _originalConnections.TryGetValue(elementId.Value, out var list) ? list : [];

public int TotalChainElements => Nodes.Count - 1;
public int MaxLevel => _levels.Count - 1;
```

**В конструктор добавить:** `_levels.Add([rootId]);` (Level 0 = root).

### 4.2. ConnectionRecord (новый)

**Файл:** `SmartCon.Core/Models/ConnectionRecord.cs`

```csharp
using Autodesk.Revit.DB;
namespace SmartCon.Core.Models;

public sealed record ConnectionRecord(
    ElementId ThisElementId,
    int ThisConnectorIndex,
    ElementId NeighborElementId,
    int NeighborConnectorIndex
);
```

### 4.3. ElementSnapshot (новый)

**Файл:** `SmartCon.Core/Models/NetworkSnapshot.cs`

> **КРИТИЧЕСКОЕ ИЗМЕНЕНИЕ vs v1:** Для FamilyInstance храним полный Transform
> (Origin + BasisX/Y/Z), а НЕ RotationAxis+RotationAngle. Восстановление через
> axis+angle хрупко. Transform — единственный надёжный способ.

```csharp
using Autodesk.Revit.DB;
namespace SmartCon.Core.Models;

public sealed record ElementSnapshot
{
    public required ElementId ElementId { get; init; }
    public required bool IsMepCurve { get; init; }
    // FamilyInstance: полный Transform
    public XYZ? FiOrigin { get; init; }
    public XYZ? FiBasisX { get; init; }
    public XYZ? FiBasisY { get; init; }
    public XYZ? FiBasisZ { get; init; }
    // MEPCurve: начало/конец Location.Curve
    public XYZ? CurveStart { get; init; }
    public XYZ? CurveEnd { get; init; }
    // Размер
    public double ConnectorRadius { get; init; }
    public ElementId? FamilySymbolId { get; init; }
    // Соединения (из графа)
    public IReadOnlyList<ConnectionRecord> Connections { get; init; } = [];
    // Reducer-ы вставленные при + (заполняется после операции)
    public List<ElementId> InsertedReducerIds { get; } = [];
}
```

### 4.4. NetworkSnapshotStore (новый)

**Файл:** `SmartCon.Core/Models/NetworkSnapshotStore.cs`

```csharp
using Autodesk.Revit.DB;
namespace SmartCon.Core.Models;

public sealed class NetworkSnapshotStore
{
    private readonly Dictionary<long, ElementSnapshot> _snapshots = new();

    public void Save(ElementSnapshot snapshot)
        => _snapshots[snapshot.ElementId.Value] = snapshot;

    public ElementSnapshot? Get(ElementId elementId)
        => _snapshots.TryGetValue(elementId.Value, out var s) ? s : null;

    public void TrackReducer(ElementId elementId, ElementId reducerId)
    {
        if (_snapshots.TryGetValue(elementId.Value, out var s))
            s.InsertedReducerIds.Add(reducerId);
    }
}
```

### 4.5. PipeConnectSessionContext (расширение)

**Файл:** `SmartCon.Core/Models/PipeConnectSessionContext.cs` (уже существует)

**Добавить:**
```csharp
/// Граф цепочки dynamic-элемента, построенный ДО disconnect. null = нет цепочки.
public ConnectionGraph? ChainGraph { get; init; }
```

> `NetworkSnapshotStore` НЕ в контексте — создаётся и живёт в ViewModel как мутабельное поле.

---

## 5. Алгоритмы

### 5.1. ElementChainIterator.BuildGraph (НОВАЯ реализация)

**Файл:** `SmartCon.Revit/Selection/ElementChainIterator.cs` — **создать**

**Реализует:** `IElementChainIterator` (интерфейс уже есть в Core)

```
ВХОД: doc, startElementId (dynamic), stopAtElements ({staticElementId})
ВЫХОД: ConnectionGraph

1. Определить "заблокированный" коннектор dynamic (ведёт к static):
   cm = GetConnectorManager(doc.GetElement(startElementId))
   excludedConnIdx = -1
   для каждого conn в cm.Connectors:
     если conn.ConnectorType == Curve → continue
     для каждого refConn в conn.AllRefs:
       если refConn.Owner != null И refConn.Owner.Id в stopAtElements:
         excludedConnIdx = (int)conn.Id
         break

2. Инициализация:
   graph = new ConnectionGraph(startElementId)  // Level 0 = [startElementId]
   visited = HashSet<ElementId>(ElementIdEqualityComparer.Instance) { startElementId }
   currentLevelIds = [startElementId]
   bfsLevel = 0

3. BFS по уровням:
   while currentLevelIds.Count > 0:
     nextLevelIds = []
     bfsLevel++

     для каждого elemId в currentLevelIds:
       elem = doc.GetElement(elemId)
       cm = GetConnectorManager(elem)
       если cm == null → continue

       для каждого conn в cm.Connectors:
         если conn.ConnectorType == ConnectorType.Curve → continue
         если elemId == startElementId И (int)conn.Id == excludedConnIdx → continue
         если !conn.IsConnected → continue

         для каждого refConn в conn.AllRefs:
           если refConn.Owner == null → continue
           neighborId = refConn.Owner.Id
           если neighborId в stopAtElements → continue
           если neighborId в visited → continue

           visited.Add(neighborId)
           nextLevelIds.Add(neighborId)
           graph.AddNode(neighborId)
           graph.AddElementAtLevel(bfsLevel, neighborId)
           graph.AddEdge(new ConnectionEdge(
             elemId, (int)conn.Id, neighborId, (int)refConn.Id))

           // Сохранить соединения ОБЕИХ сторон
           graph.SaveConnection(elemId, new ConnectionRecord(
             elemId, (int)conn.Id, neighborId, (int)refConn.Id))
           graph.SaveConnection(neighborId, new ConnectionRecord(
             neighborId, (int)refConn.Id, elemId, (int)conn.Id))

     currentLevelIds = nextLevelIds

4. return graph

Вспомогательный метод:
  static ConnectorManager? GetConnectorManager(Element? elem)
    => elem switch {
         FamilyInstance fi => fi.MEPModel?.ConnectorManager,
         MEPCurve mc       => mc.ConnectorManager,
         _                 => null };
```

### 5.2. GetChainEndConnectors (НОВАЯ реализация)

```
ВХОД: doc, graph
ВЫХОД: List<ConnectorProxy>

для каждого elemId в graph.Nodes:
  cm = GetConnectorManager(doc.GetElement(elemId))
  если cm == null → continue
  для каждого conn в cm.Connectors:
    если conn.ConnectorType == Curve → continue
    если conn.IsConnected → continue
    result.Add(conn.ToProxy())  // существующий extension
return result
```

### 5.3. CaptureSnapshot — захват состояния элемента

> Snapshot захватывается **ДО** операции + для элементов уровня.
> При − элемент возвращается в точности в ИСХОДНОЕ состояние (до +).

```
ВХОД: doc, elemId, graph, connSvc
ВЫХОД: ElementSnapshot

1. elem = doc.GetElement(elemId)
2. isMepCurve = elem is MEPCurve

3. если elem is FamilyInstance fi:
     t = fi.GetTransform()
     FiOrigin=t.Origin, FiBasisX=t.BasisX, FiBasisY=t.BasisY, FiBasisZ=t.BasisZ

   если elem is MEPCurve mc:
     line = (mc.Location as LocationCurve).Curve as Line
     CurveStart=line.GetEndPoint(0), CurveEnd=line.GetEndPoint(1)

4. ConnectorRadius = первый не-Curve коннектор .Radius (через connSvc.GetAllConnectors)
5. FamilySymbolId = (elem as FamilyInstance)?.Symbol.Id
6. Connections = graph.GetOriginalConnections(elemId)
7. return new ElementSnapshot { ... }
```

### 5.4. Кнопка +: IncrementChainDepth

> **КРИТИЧЕСКОЕ ИЗМЕНЕНИЕ vs v1:** Добавлен шаг **ПЕРЕМЕЩЕНИЯ** элементов.
> В v1 после DisconnectAll сразу шёл ConnectTo — но ConnectTo в Revit требует
> пространственного совпадения коннекторов. После перемещения Dynamic
> элементы цепочки остались на СТАРЫХ позициях → их нужно переместить
> к текущим позициям родительских коннекторов через ConnectorAligner.

> **ИСПРАВЛЕНИЕ DisconnectAll:** В v1 был `DisconnectAllFromConnector(doc, elemId, 0)`
> с хардкодом индекса 0. Правильно — итерировать ВСЕ коннекторы элемента.

```
1. nextLevel = _chainDepth + 1
   если nextLevel >= graph.Levels.Count → return
   levelElements = graph.Levels[nextLevel]

2. Захватить snapshot КАЖДОГО элемента уровня ДО модификации:
   для каждого elemId в levelElements:
     snapshot = CaptureSnapshot(doc, elemId, graph, _connSvc)
     _snapshotStore.Save(snapshot)

3. _groupSession.RunInTransaction("Цепочка: уровень {nextLevel}", doc =>
   {
     для каждого elemId в levelElements:

       // ── a. Disconnect от ВСЕХ соседей ──
       allConns = _connSvc.GetAllConnectors(doc, elemId)
       для каждого c в allConns:
         если !c.IsFree:
           _connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex)

       // ── b. Найти ребро к родителю (уровень N-1) ──
       edge = FindEdgeToParent(elemId, nextLevel, graph)
       если edge == null → continue
       // edge содержит: parentId, parentConnIdx, elemId, elemConnIdx

       // ── c. ПЕРЕМЕСТИТЬ элемент к родительскому коннектору ──
       // (ОТСУТСТВОВАЛО В V1 — это критический шаг!)
       parentProxy = _connSvc.RefreshConnector(doc, edge.ParentId, edge.ParentConnIdx)
       elemProxy = _connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx)

       если parentProxy != null И elemProxy != null:
         alignResult = ConnectorAligner.ComputeAlignment(
           parentProxy.OriginVec3, parentProxy.BasisZVec3, parentProxy.BasisXVec3,
           elemProxy.OriginVec3, elemProxy.BasisZVec3, elemProxy.BasisXVec3)

         если !VectorUtils.IsZero(alignResult.InitialOffset):
           _transformSvc.MoveElement(doc, elemId, alignResult.InitialOffset)
         если alignResult.BasisZRotation is { } bzRot:
           _transformSvc.RotateElement(doc, elemId,
             alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians)
         если alignResult.BasisXSnap is { } bxSnap:
           _transformSvc.RotateElement(doc, elemId,
             alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians)

         doc.Regenerate()

         // Коррекция позиции (после поворотов Origin мог сместиться)
         refreshed = _connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx)
         если refreshed != null:
           correction = parentProxy.OriginVec3 - refreshed.OriginVec3
           если !VectorUtils.IsZero(correction):
             _transformSvc.MoveElement(doc, elemId, correction)
         doc.Regenerate()

       // ── d. AdjustSize + InsertReducer (ИСПРАВЛЕНО v2.4) ──
       // КЛЮЧЕВЫЕ ИЗМЕНЕНИЯ vs v2.3:
       //   1. targetRadius = DynamicConnector.Radius (НЕ parentProxy.Radius)
       //   2. TrySetConnectorRadius вызывается для ВСЕХ типов (включая MEPCurve)
       //   3. Верификация фактического радиуса после Regenerate
       //   4. Для многопортовых фитингов — подгонка ВСЕХ коннекторов в графе

       ElementId? reducerId = null
       targetRadius = _ctx.DynamicConnector.Radius

       если parentProxy != null:
         elemRefreshed = _connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx)
         если elemRefreshed != null И |targetRadius - elemRefreshed.Radius| > 1e-5:

           // d.1. Подгонка parent-facing коннектора (единый путь для всех типов)
           _paramResolver.TrySetConnectorRadius(
             doc, elemId, edge.ElemConnIdx, targetRadius)

           // d.2. Для FamilyInstance: подогнать ВСЕ коннекторы элемента в графе цепочки
           elem = doc.GetElement(elemId)
           если elem is FamilyInstance:
             allElemConns = _connSvc.GetAllConnectors(doc, elemId)
             для каждого c в allElemConns:
               если c.ConnectorIndex != edge.ElemConnIdx:  // уже обработан выше
                 // Проверить: коннектор участвует в графе?
                 если graph.Edges.Any(e =>
                   (comparer.Equals(e.FromElementId, elemId) && e.FromConnectorIndex == c.ConnectorIndex) ||
                   (comparer.Equals(e.ToElementId, elemId) && e.ToConnectorIndex == c.ConnectorIndex)):
                   _paramResolver.TrySetConnectorRadius(doc, elemId, c.ConnectorIndex, targetRadius)

           doc.Regenerate()

           // d.3. ВЕРИФИКАЦИЯ фактического радиуса (Revit мог округлить/ограничить)
           elemRefreshed = _connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx)
           если elemRefreshed != null И |targetRadius - elemRefreshed.Radius| > 1e-5:
             // Подгонка НЕ удалась → InsertReducer
             reducerId = _networkMover.InsertReducer(doc, parentProxy, elemRefreshed)
             если reducerId != null: _snapshotStore.TrackReducer(elemId, reducerId)

         doc.Regenerate()

       // ── e. ConnectTo ──
       // КРИТИЧЕСКОЕ ИСПРАВЛЕНИЕ v2.2: если вставлен reducer,
       // соединяем parent↔reducer↔child, а НЕ parent↔child напрямую.
       если reducerId != null:
         // Reducer уже выровнен к parentProxy (InsertReducer шаг 3+5).
         // Найти коннекторы reducer:
         rConns = _connSvc.GetAllFreeConnectors(doc, reducerId)
         rConn1 = ближайший к parentProxy.Origin
         rConn2 = другой

         // Переместить child к rConn2 (reducer мог сдвинуть точку соединения):
         childRefreshed = _connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx)
         если childRefreshed != null И rConn2 != null:
           childAlign = ConnectorAligner.ComputeAlignment(
             rConn2.OriginVec3, rConn2.BasisZVec3, rConn2.BasisXVec3,
             childRefreshed.OriginVec3, childRefreshed.BasisZVec3, childRefreshed.BasisXVec3)
           применить childAlign (Move + Rotate)
           doc.Regenerate()

         // Соединить: parent ↔ reducer_conn1
         _connSvc.ConnectTo(doc, edge.ParentId, edge.ParentConnIdx,
           reducerId, rConn1.ConnectorIndex)
         // Соединить: reducer_conn2 ↔ child
         _connSvc.ConnectTo(doc, reducerId, rConn2.ConnectorIndex,
           elemId, edge.ElemConnIdx)
       иначе:
         // Прямое соединение (DN совпадает или AdjustSize успешен)
         _connSvc.ConnectTo(doc, edge.ParentId, edge.ParentConnIdx,
           elemId, edge.ElemConnIdx)

     doc.Regenerate()
   })

4. _chainDepth = nextLevel
5. UpdateChainUI()

Вспомогательный метод FindEdgeToParent(elemId, level, graph):
  comparer = ElementIdEqualityComparer.Instance
  parentIds = HashSet(graph.Levels[level - 1], comparer)
  для каждого edge в graph.Edges:
    если comparer.Equals(edge.ToElementId, elemId) И parentIds.Contains(edge.FromElementId):
      return (ParentId=edge.FromElementId, ParentConnIdx=edge.FromConnectorIndex,
              ElemConnIdx=edge.ToConnectorIndex)
    если comparer.Equals(edge.FromElementId, elemId) И parentIds.Contains(edge.ToElementId):
      return (ParentId=edge.ToElementId, ParentConnIdx=edge.ToConnectorIndex,
              ElemConnIdx=edge.FromConnectorIndex)
  return null
```

### 5.5. Кнопка −: DecrementChainDepth

```
1. если _chainDepth <= 0 → return
   levelElements = graph.Levels[_chainDepth]

2. _groupSession.RunInTransaction("Цепочка: откат уровня {_chainDepth}", doc =>
   {
     для каждого elemId в levelElements:

       // ── a. Disconnect от всех текущих соединений ──
       allConns = _connSvc.GetAllConnectors(doc, elemId)
       для каждого c в allConns:
         если !c.IsFree:
           _connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex)

       // ── b. Удалить reducer-ы ──
       snapshot = _snapshotStore.Get(elemId)
       если snapshot != null:
         для каждого reducerId в snapshot.InsertedReducerIds:
           rConns = _connSvc.GetAllConnectors(doc, reducerId)
           для каждого rc в rConns:
             если !rc.IsFree: _connSvc.DisconnectAllFromConnector(doc, reducerId, rc.ConnectorIndex)
           _fittingInsertSvc.DeleteElement(doc, reducerId)

       // ── c. Восстановить позицию из snapshot ──
       если snapshot == null → continue
       elem = doc.GetElement(elemId)

       если elem is FamilyInstance fi И snapshot.FiOrigin != null:
         // Восстановить FamilySymbol (размер)
         если snapshot.FamilySymbolId != null И fi.Symbol.Id != snapshot.FamilySymbolId:
           fi.ChangeTypeId(snapshot.FamilySymbolId)

         // Восстановить позицию
         если fi.Location is LocationPoint lp:
           lp.Point = snapshot.FiOrigin
         doc.Regenerate()

         // Восстановить ориентацию через ConnectorAligner
         // ПРИМЕЧАНИЕ: lp.Point уже = snapshot.FiOrigin (шаг выше).
         // ComputeAlignment первый аргумент (static) = snapshot → RotationCenter = snapshot.FiOrigin.
         // BasisX/Y/Z ещё старые → ComputeAlignment вычислит поворот от текущего к целевому.
         currentT = fi.GetTransform()
         reAlign = ConnectorAligner.ComputeAlignment(
           Vec3(snapshot.FiOrigin), Vec3(snapshot.FiBasisZ), Vec3(snapshot.FiBasisX),
           Vec3(currentT.Origin), Vec3(currentT.BasisZ), Vec3(currentT.BasisX))

         // reAlign.RotationCenter = snapshot.FiOrigin (совпадает с текущим lp.Point)
         применить reAlign (InitialOffset, BasisZRotation, BasisXSnap)
         doc.Regenerate()

         // Финальная коррекция позиции
         если fi.Location is LocationPoint lp2:
           correction = snapshot.FiOrigin - lp2.Point → MoveElement
           doc.Regenerate()

       если elem is MEPCurve mc И snapshot.CurveStart != null:
         если mc.Location is LocationCurve lc:
           lc.Curve = Line.CreateBound(snapshot.CurveStart, snapshot.CurveEnd)
         doc.Regenerate()

       // ── d. Восстановить исходные соединения (с элементами НЕ в текущей цепочке) ──
       для каждого connRecord в snapshot.Connections:
         neighborId = connRecord.NeighborElementId
         // Восстанавливаем только соединения с элементами за пределами текущей цепочки
         если IsInCurrentChain(neighborId, _chainDepth - 1) → continue

         neighborConn = _connSvc.RefreshConnector(doc, neighborId, connRecord.NeighborConnectorIndex)
         если neighborConn == null → continue
         если !neighborConn.IsFree:
           _connSvc.DisconnectAllFromConnector(doc, neighborId, connRecord.NeighborConnectorIndex)

         _connSvc.ConnectTo(doc,
           connRecord.ThisElementId, connRecord.ThisConnectorIndex,
           connRecord.NeighborElementId, connRecord.NeighborConnectorIndex)

     doc.Regenerate()
   })

3. _chainDepth--
4. UpdateChainUI()

Вспомогательный метод IsInCurrentChain(elemId, maxLevel):
  для level = 0 до maxLevel включительно:
    если graph.Levels[level].Any(id => ElementIdEqualityComparer.Instance.Equals(id, elemId)):
      return true
  return false
  // ВАЖНО: List.Contains использует reference equality для ElementId.
  // Нужен ElementIdEqualityComparer (сравнение по .Value).
```

### 5.6. ExecuteRotate (модификация существующего)

**Файл:** `PipeConnectEditorViewModel.cs` — изменить формирование `idsToRotate`

```
1. idsToRotate = [dynamicElementId]
2. если currentFittingId != null: idsToRotate.Add(currentFittingId)

3. // ═══ НОВОЕ: элементы цепочки до _chainDepth ═══
   если _chainGraph != null И _chainDepth > 0:
     для level = 1 до _chainDepth:
       для каждого elemId в _chainGraph.Levels[level]:
         idsToRotate.Add(elemId)
     // + reducer-ы всех уровней
     для level = 1 до _chainDepth:
       для каждого elemId в _chainGraph.Levels[level]:
         snapshot = _snapshotStore.Get(elemId)
         если snapshot != null:
           для каждого reducerId в snapshot.InsertedReducerIds:
             idsToRotate.Add(reducerId)

4. _transformSvc.RotateElements(doc, idsToRotate, axisOrigin, axisDir, radians)
   doc.Regenerate()

5. // ═══ НОВЫЙ КОД: GlobalYSnap ТОЛЬКО для dynamic ═══
   // В текущем ExecuteRotate (строки 395-450) GlobalYSnap ОТСУТСТВУЕТ.
   // Нужно ДОБАВИТЬ по аналогии с Init (строки 261-296):
   dynElem = doc.GetElement(dynamicElementId)
   если dynElem is FamilyInstance fiForSnap:
     t = fiForSnap.GetTransform()
     elemBasisY = Vec3(t.BasisY)
     staticBZ = _ctx.StaticConnector.BasisZVec3
     globalYSnap = ConnectorAligner.ComputeGlobalYAlignmentSnap(
       staticBZ, elemBasisY, axisOrigin)
     если globalYSnap != null:
       _transformSvc.RotateElement(doc, dynamicElementId,
         axisOrigin, globalYSnap.Axis, globalYSnap.AngleRadians)
   // НЕ применяем GlobalYSnap к элементам цепочки — они rigid body

6. doc.Regenerate()
```

---

## 6. UI спецификация

### 6.1. Замена CheckBox на счётчик глубины

**Файл:** `SmartCon.PipeConnect/Views/PipeConnectEditorView.xaml`

**Было (строки ~254-259):**
```xml
<CheckBox Grid.Row="10"
          Content="Переместить всю сеть"
          IsChecked="{Binding MoveEntireChain, Mode=TwoWay}"
          IsEnabled="{Binding IsSessionActive}"
          Foreground="#444"/>
```

**Стало:**

> **ВАЖНО (обновлено v2.4):** Конвертер `BoolToVisibilityConverter` существует в `SmartCon.UI\Converters\`,
> но при реализации были проблемы с подтягиванием из другой сборки через XAML.
> **Рекомендация:** создать **локальный** конвертер в `SmartCon.PipeConnect\Converters\BoolToVisibilityConverter.cs`
> (простой 1-классовый файл) и зарегистрировать в ресурсах окна:
> ```xml
> <Window.Resources>
>     <converters:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
> </Window.Resources>
> ```
> и namespace: `xmlns:converters="clr-namespace:SmartCon.PipeConnect.Converters"`

```xml
<!-- ── Глубина цепочки ─────────────────────────────────────── -->
<Grid Grid.Row="10" Margin="0,4,0,0"
      Visibility="{Binding HasChain, Converter={StaticResource BoolToVisibility}}">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="8"/>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="4"/>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="4"/>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="8"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0" Text="Сеть:"
               VerticalAlignment="Center" Foreground="#444" FontSize="12"/>
    <Button Grid.Column="2" Content="−" Width="28" Height="28"
            Command="{Binding DecrementChainDepthCommand}"
            Style="{StaticResource ModernButton}" FontSize="14" Padding="0"
            ToolTip="Отсоединить последний уровень"/>
    <TextBlock Grid.Column="4" Text="{Binding ChainDepth}" Width="24"
               TextAlignment="Center" VerticalAlignment="Center"
               FontSize="14" FontWeight="SemiBold" Foreground="#333"/>
    <Button Grid.Column="6" Content="+" Width="28" Height="28"
            Command="{Binding IncrementChainDepthCommand}"
            Style="{StaticResource ModernButton}" FontSize="14" Padding="0"
            ToolTip="Присоединить следующий уровень"/>
    <TextBlock Grid.Column="8" Text="{Binding ChainDepthHint}"
               Foreground="#888" FontSize="10"
               VerticalAlignment="Center" TextWrapping="NoWrap"/>
</Grid>
```

### 6.2. ViewModel свойства (новые)

```csharp
// ── Chain ───────────────────────────────────────────────
private ConnectionGraph? _chainGraph;
private readonly NetworkSnapshotStore _snapshotStore = new();

[ObservableProperty] private int    _chainDepth = 0;
[ObservableProperty] private string _chainDepthHint = "нет цепочки";
[ObservableProperty] private bool   _hasChain;
```

### 6.3. ViewModel команды (новые, CommunityToolkit.Mvvm)

```csharp
[RelayCommand(CanExecute = nameof(CanIncrementChain))]
private void IncrementChainDepth() { ... }  // см. алгоритм 5.4

[RelayCommand(CanExecute = nameof(CanDecrementChain))]
private void DecrementChainDepth() { ... }  // см. алгоритм 5.5

private const int MaxChainLevel = 30; // линейная сеть: 10 труб + 10 отводов = 20 уровней BFS

private bool CanIncrementChain()
    => IsSessionActive && !IsBusy
    && _chainGraph != null
    && _chainDepth < _chainGraph.MaxLevel
    && _chainDepth < MaxChainLevel;

private bool CanDecrementChain()
    => IsSessionActive && !IsBusy
    && _chainDepth > 0;
```

### 6.4. UpdateChainUI — обновление hint и кнопок

```csharp
private void UpdateChainUI()
{
    if (_chainGraph == null || _chainGraph.TotalChainElements == 0)
    {
        ChainDepthHint = "нет цепочки";
        HasChain = false;
        return;
    }
    HasChain = true;
    int total = _chainGraph.TotalChainElements;
    int maxLvl = _chainGraph.MaxLevel;
    int attached = 0;
    for (int l = 1; l <= _chainDepth; l++)
        attached += _chainGraph.Levels[l].Count;

    ChainDepthHint = $"{attached}/{total} в {_chainDepth}/{maxLvl} ур.";

    IncrementChainDepthCommand.NotifyCanExecuteChanged();
    DecrementChainDepthCommand.NotifyCanExecuteChanged();
}
```

Примеры hint: `"0/7 в 0/3 ур."`, `"3/7 в 2/3 ур."`, `"7/7 в 3/3 ур."`, `"нет цепочки"`

---

## 7. Сервис INetworkMover (новый)

> **Изменение vs v1:** Вместо `INetworkSizeAdjuster` (слишком узкий) создаём `INetworkMover` —
> сервис который умеет вставлять reducer между двумя коннекторами с разным DN.
> Подгонка размера FamilyInstance делается напрямую через `IParameterResolver`
> в алгоритме 5.4 (шаг d) — отдельный сервис для этого не нужен.

### 7.1. Интерфейс

**Файл:** `SmartCon.Core/Services/Interfaces/INetworkMover.cs` (новый)

```csharp
using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Вставка reducer-а между двумя коннекторами с разным DN.
/// </summary>
public interface INetworkMover
{
    /// <summary>
    /// Вставить reducer между parentConn и childConn.
    /// Reducer выравнивается к parentConn. Возвращает ElementId или null.
    /// </summary>
    ElementId? InsertReducer(Document doc,
        ConnectorProxy parentConn, ConnectorProxy childConn);
}
```

### 7.2. Реализация

**Файл:** `SmartCon.Revit/Network/NetworkMover.cs` (новый)

**Зависимости (через DI):** `IFittingInsertService`, `IFittingMapper`,
`IConnectorService`, `ITransformService`, `IParameterResolver`

```
InsertReducer(doc, parentConn, childConn):

1. Найти reducer в маппинге:
   // ИСПРАВЛЕНИЕ v2.3: reducer соединяет ОДИН тип коннектора (Pipe↔Pipe),
   // поэтому оба аргумента — ConnectionTypeCode РОДИТЕЛЯ.
   // В ViewModel (строка 98): rule.FromType.Value == rule.ToType.Value
   mappings = _fittingMapper.GetMappings(parentConn.ConnectionTypeCode,
                                          parentConn.ConnectionTypeCode)
   reducerRule = mappings.FirstOrDefault(m => m.ReducerFamilies.Count > 0)
   если reducerRule == null → return null
   reducerFamily = reducerRule.ReducerFamilies[0]

2. Вставить reducer (СУЩЕСТВУЮЩИЙ IFittingInsertService.InsertFitting):
   reducerId = _fittingInsertSvc.InsertFitting(
     doc, reducerFamily.FamilyName, reducerFamily.SymbolName, parentConn.Origin)
   если reducerId == null → return null

3. Выровнять к parentConn (СУЩЕСТВУЮЩИЙ AlignFittingToStatic):
   // AlignFittingToStatic выравнивает ОДИН коннектор reducer к parentConn,
   // и возвращает ВТОРОЙ коннектор (смотрящий на child) — именно то что нужно.
   reducerConn2 = _fittingInsertSvc.AlignFittingToStatic(
     doc, reducerId, parentConn, _transformSvc, _connSvc)

4. Подогнать размер (СУЩЕСТВУЮЩИЙ TrySetFittingTypeForPair):
   allConns = _connSvc.GetAllFreeConnectors(doc, reducerId)
   если allConns.Count >= 2:
     conn1 = ближайший к parentConn.Origin
     conn2 = другой
     _paramResolver.TrySetFittingTypeForPair(doc, reducerId,
       conn1.ConnectorIndex, parentConn.Radius,
       conn2.ConnectorIndex, childConn.Radius)

5. Повторно выровнять (после смены размера геометрия могла измениться):
   _fittingInsertSvc.AlignFittingToStatic(
     doc, reducerId, parentConn, _transformSvc, _connSvc)

6. return reducerId
```

### 7.3. Использование существующих сервисов (справка)

| Существующий сервис | Что используется в Chain | Файл реализации |
|---|---|---|
| `IParameterResolver` | `TrySetConnectorRadius` (AdjustSize), `TrySetFittingTypeForPair` (reducer) | `RevitParameterResolver.cs` |
| `IFittingInsertService` | `InsertFitting`, `AlignFittingToStatic`, `DeleteElement` | `RevitFittingInsertService.cs` |
| `IFittingMapper` | `GetMappings` (найти reducer семейство) | `FittingMapper.cs` |
| `IConnectorService` | `GetAllConnectors`, `DisconnectAllFromConnector`, `ConnectTo`, `RefreshConnector` | `ConnectorService.cs` |
| `ITransformService` | `MoveElement`, `RotateElement`, `RotateElements` | `RevitTransformService.cs` |
| `ITransactionGroupSession` | `RunInTransaction` (внутри открытой TransactionGroup) | `RevitTransactionGroupSession.cs` |
| `ConnectorAligner` | `ComputeAlignment` (выравнивание элемента к коннектору) | `ConnectorAligner.cs` |

### 7.4. Рефакторинг `RevitParameterResolver.TrySetConnectorRadius` (НОВОЕ v2.4)

> **Файл:** `SmartCon.Revit/Parameters/RevitParameterResolver.cs`
>
> **Проблема обнаружена при реализации:** Строки 306-311 — если `dep.IsInstance=true`
> и прямая запись не удалась (ReadOnly или SolveFor вернул null), код возвращает `false`
> **БЕЗ попытки** `TryChangeTypeTo`. Это критично для отводов/тройников: их параметр DN
> экземплярный, но значения ограничены lookup-таблицей ТИПОРАЗМЕРА. Смена типа может
> разблокировать нужный DN.

**Требуемое изменение (после строки 311):**

```csharp
// БЫЛО:
if (dep.IsInstance)
{
    SmartConLogger.Lookup("  dep.IsInstance=True: все ветки исчерпаны → return false");
    return false;  // ← БАГ: не пробует TryChangeTypeTo
}

// СТАЛО:
if (dep.IsInstance)
{
    // Fallback: прямая запись не сработала → пробуем сменить типоразмер.
    // У многих фитингов IsInstance=true, но допустимые значения
    // определяются lookup-таблицей типоразмера.
    SmartConLogger.Lookup("  dep.IsInstance=True: fallback → TryChangeTypeTo...");
    return TryChangeTypeTo(doc, elementId, connectorIndex, targetRadiusInternalUnits);
}
```

**Почему это безопасно:** `TryChangeTypeTo` использует `SubTransaction` для каждой попытки —
если нужного типоразмера нет, элемент не изменится.

---

## 8. Интеграция с PipeConnectCommand

**Файл:** `SmartCon.PipeConnect/Commands/PipeConnectCommand.cs`

**Место:** В `Execute()`, **ПОСЛЕ S5** (подбор фитингов), **ДО** создания `PipeConnectSessionContext`.

> `BuildGraph` — read-only операция (AllRefs), не требует транзакции.
> Disconnect происходит позже в `ViewModel.Init()` внутри TransactionGroup.

**Добавить:**

```csharp
// ── S6.1: построить граф цепочки dynamic (ДО disconnect) ──────
var chainIterator = ServiceHost.GetService<IElementChainIterator>();
var stopAt = new HashSet<ElementId>(new ElementIdEqualityComparer())
{
    staticPick.Value.ElementId
};
var chainGraph = chainIterator.BuildGraph(doc, dynamicPick.Value.ElementId, stopAt);
SmartConLogger.Info($"[Chain] Граф: {chainGraph.TotalChainElements} элементов, " +
    $"{chainGraph.MaxLevel} уровней");

// ── S6.2: ПРОГРЕВ КЕША (НОВОЕ v2.4) ─────────────────────────
// КРИТИЧНО: GetConnectorRadiusDependencies для FamilyInstance с ReadOnly-параметрами
// вызывает doc.EditFamily() — это ЗАПРЕЩЕНО внутри открытой транзакции.
// Код имеет guard (!doc.IsModifiable), но без кеша теряется формульный путь →
// fallback на TryChangeTypeTo (менее точный).
// Поэтому ВСЕ deps кешируются ЗДЕСЬ, ДО открытия UI и транзакций.
foreach (var level in chainGraph.Levels.Values)
{
    foreach (var elemId in level)
    {
        var elem = doc.GetElement(elemId);
        if (elem is null) continue;
        var cm = elem switch
        {
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            MEPCurve mc       => mc.ConnectorManager,
            _                 => null
        };
        if (cm is null) continue;
        foreach (Connector c in cm.Connectors)
        {
            if (c.ConnectorType == ConnectorType.Curve) continue;
            paramResolver.GetConnectorRadiusDependencies(doc, elemId, (int)c.Id);
        }
    }
}
SmartConLogger.Info($"[Chain] Кеш deps прогрет для {chainGraph.TotalChainElements} элементов");
```

**Изменить создание контекста:**
```csharp
var sessionCtx = new PipeConnectSessionContext
{
    StaticConnector         = staticProxy,
    DynamicConnector        = dynamicProxy,
    AlignResult             = alignResult,
    ParamTargetRadius       = plan.Skip ? null : plan.TargetRadius,
    ParamExpectNeedsAdapter = plan.ExpectNeedsAdapter,
    ProposedFittings        = proposed.ToList(),
    ChainGraph              = chainGraph,  // ← НОВОЕ
};
```

**Изменить создание ViewModel — добавить `INetworkMover`:**
```csharp
// ПРИМЕЧАНИЕ: все ServiceHost.GetService вызываются в Execute() ДО ExternalEventActionQueue.
// networkMover доступен в замыкании — тот же паттерн что и остальные сервисы (строки 41-56).
var networkMover = ServiceHost.GetService<INetworkMover>();

// Полная сигнатура конструктора (НОВЫЙ параметр — networkMover):
var vm = new PipeConnectEditorViewModel(
    sessionCtx,        // PipeConnectSessionContext (теперь с ChainGraph)
    doc,               // Document
    txService,         // ITransactionService
    connectorSvc,      // IConnectorService
    transformSvc,      // ITransformService
    fittingInsertSvc,  // IFittingInsertService
    paramResolver,     // IParameterResolver
    sizeResolver,      // IDynamicSizeResolver
    networkMover);     // INetworkMover ← НОВЫЙ
```

**Соответствующий конструктор ViewModel (полная сигнатура):**
```csharp
public PipeConnectEditorViewModel(
    PipeConnectSessionContext  ctx,
    Document                   doc,
    ITransactionService        txService,
    IConnectorService          connSvc,
    ITransformService          transformSvc,
    IFittingInsertService      fittingInsertSvc,
    IParameterResolver         paramResolver,
    IDynamicSizeResolver       sizeResolver,
    INetworkMover              networkMover)    // ← НОВЫЙ
{
    // ... существующая инициализация ...
    _networkMover = networkMover;                // ← НОВОЕ поле
    _chainGraph   = ctx.ChainGraph;              // ← НОВОЕ
    UpdateChainUI();                             // ← НОВЫЙ вызов
    // ПРИМЕЧАНИЕ: _groupSession НЕ передаётся в конструктор.
    // Она создаётся в Init(): _groupSession = _txService.BeginGroupSession("PipeConnect")
}
```

---

## 9. Регистрация в DI

**Файл:** `SmartCon.App/DI/ServiceRegistrar.cs`

**Добавить в `Register()`:**
```csharp
// --- Chain (Phase 7) ---
services.AddSingleton<IElementChainIterator, ElementChainIterator>();
services.AddSingleton<INetworkMover, NetworkMover>();
```

> **ВАЖНО:** `ElementIdEqualityComparer` сейчас `internal` в `ConnectionGraph.cs`.
> **Действие:** Изменить на `public sealed class` — он нужен в `PipeConnectCommand` (другая сборка).
> Альтернатива: вынести в отдельный файл `SmartCon.Core/Models/ElementIdEqualityComparer.cs`.

---

## 10. Удаление устаревшего кода

### 10.1. PipeConnectionSession.MoveEntireChain
**Файл:** `SmartCon.Core/Models/PipeConnectionSession.cs` — удалить:
```csharp
public bool MoveEntireChain { get; set; }  // и MoveEntireChain = false в Reset()
```

### 10.2. PipeConnectEditorViewModel._moveEntireChain
**Файл:** `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` — удалить:
```csharp
[ObservableProperty] private bool _moveEntireChain;
```

### 10.3. PipeConnectEditorView.xaml CheckBox
Заменить на UI глубины (секция 6.1).

---

## 11. Таблица файлов (итоговая)

| # | Файл | Действие | Описание |
|---|------|----------|----------|
| 1 | `SmartCon.Core/Models/ConnectionRecord.cs` | **Создать** | Record: ThisElementId, ThisConnectorIndex, NeighborElementId, NeighborConnectorIndex |
| 2 | `SmartCon.Core/Models/NetworkSnapshot.cs` | **Создать** | ElementSnapshot с полным Transform (Origin+BasisX/Y/Z) |
| 3 | `SmartCon.Core/Models/NetworkSnapshotStore.cs` | **Создать** | Dictionary-хранилище snapshot-ов + TrackReducer |
| 4 | `SmartCon.Core/Services/Interfaces/INetworkMover.cs` | **Создать** | InsertReducer(doc, parentConn, childConn) |
| 5 | `SmartCon.Revit/Selection/ElementChainIterator.cs` | **Создать** | BFS по уровням, SaveConnection, excludedConnIdx |
| 6 | `SmartCon.Revit/Network/NetworkMover.cs` | **Создать** | InsertReducer через IFittingInsertService + IParameterResolver |
| 7 | `SmartCon.Core/Models/ConnectionGraph.cs` | **Изменить** | +Levels, +SaveConnection, +GetOriginalConnections, +TotalChainElements, +MaxLevel. **`ElementIdEqualityComparer` → `public`** |
| 8 | `SmartCon.Core/Models/PipeConnectSessionContext.cs` | **Изменить** | +ChainGraph |
| 9 | `SmartCon.PipeConnect/Commands/PipeConnectCommand.cs` | **Изменить** | BuildGraph + **Pre-caching deps (S6.2)** + передать ChainGraph + INetworkMover |
| 10 | `SmartCon.App/DI/ServiceRegistrar.cs` | **Изменить** | +IElementChainIterator, +INetworkMover |
| 11 | `SmartCon.PipeConnect/Views/PipeConnectEditorView.xaml` | **Изменить** | CheckBox → Grid с [−] depth [+] hint |
| 11a | `SmartCon.PipeConnect/Converters/BoolToVisibilityConverter.cs` | **Создать** | Локальный конвертер (проблемы с cross-assembly XAML) |
| 12 | `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` | **Изменить** | +_networkMover, +_chainGraph, +_snapshotStore, +chainDepth, +/−, +InitChain, +CaptureSnapshot, +FindEdgeToParent, +IsInCurrentChain, +UpdateChainUI, изменить ExecuteRotate, −MoveEntireChain |
| 13 | `SmartCon.Core/Models/PipeConnectionSession.cs` | **Изменить** | −MoveEntireChain, −Reset().MoveEntireChain |
| 14 | `SmartCon.Revit/Parameters/RevitParameterResolver.cs` | **Изменить** | Строки 306-311: fallback `dep.IsInstance=true` → `TryChangeTypeTo` (секция 7.4) |

---

## 12. Порядок реализации

### Фаза 1: Модели (Core, без Revit API)

1. Создать `ConnectionRecord.cs`
2. Расширить `ConnectionGraph.cs` — `_levels`, `_originalConnections`, `SaveConnection`, `GetOriginalConnections`, `Levels`, `TotalChainElements`, `MaxLevel`, `AddElementAtLevel`
3. Создать `NetworkSnapshot.cs` — `ElementSnapshot` с полным Transform
4. Создать `NetworkSnapshotStore.cs` — `Save`, `Get`, `TrackReducer`

**Верификация:** проект `SmartCon.Core` компилируется.

### Фаза 2: Итератор (Revit)

5. Создать `ElementChainIterator.cs` — реализация `IElementChainIterator` (BFS по уровням, excludedConnIdx, SaveConnection для обеих сторон)
6. Зарегистрировать `IElementChainIterator → ElementChainIterator` в `ServiceRegistrar.cs`

**Верификация:** BuildGraph возвращает корректный граф на тестовой модели.

### Фаза 3: NetworkMover (Revit)

7. Создать `INetworkMover.cs` (интерфейс в Core)
8. Создать `NetworkMover.cs` (реализация в Revit) — `InsertReducer`
9. Зарегистрировать `INetworkMover → NetworkMover` в `ServiceRegistrar.cs`

**Верификация:** InsertReducer вставляет и выравнивает reducer корректно.

### Фаза 3.5: Рефакторинг RevitParameterResolver (НОВОЕ v2.4)

7a. Изменить `RevitParameterResolver.cs` строки 306-311: fallback `dep.IsInstance=true` → `TryChangeTypeTo` (секция 7.4)

**Верификация:** Отвод с экземплярным ReadOnly-параметром DN меняет размер через смену типоразмера.

### Фаза 4: Команда и контекст

10. Добавить `ChainGraph` в `PipeConnectSessionContext.cs`
11. Изменить `PipeConnectCommand.cs`:
    - Вызвать `BuildGraph` после S5
    - **Добавить Pre-caching (S6.2):** цикл по всем коннекторам графа → `GetConnectorRadiusDependencies` (ВНЕ транзакции!)
    - Передать ChainGraph в контекст
12. Добавить `INetworkMover` в создание ViewModel

**Верификация:** PipeConnect запускается, граф построен (лог), кеш прогрет (лог), окно открывается.

### Фаза 5: UI + ViewModel

13. Создать `SmartCon.PipeConnect/Converters/BoolToVisibilityConverter.cs` (локальный)
13a. Изменить `PipeConnectEditorView.xaml` — заменить CheckBox на Grid с [−] depth [+]
14. Изменить `PipeConnectEditorViewModel.cs`:
    - Добавить поля: `_chainGraph`, `_snapshotStore`, `_chainDepth`, `_chainDepthHint`, `_hasChain`
    - Добавить зависимость: `INetworkMover _networkMover`
    - Инициализация в конструкторе: `_chainGraph = ctx.ChainGraph; UpdateChainUI();`
    - Добавить `IncrementChainDepth()` (алгоритм 5.4)
    - Добавить `DecrementChainDepth()` (алгоритм 5.5)
    - Добавить `CaptureSnapshot()` (алгоритм 5.3)
    - Добавить `FindEdgeToParent()`, `IsInCurrentChain()`, `UpdateChainUI()`
    - Изменить `ExecuteRotate()` — добавить элементы цепочки в idsToRotate (алгоритм 5.6)

**Верификация:** UI показывает счётчик, +/− работают, Rotate вращает цепочку.

### Фаза 6: Уборка

15. Удалить `_moveEntireChain` из ViewModel
16. Удалить `MoveEntireChain` из `PipeConnectionSession.cs`
17. Удалить CheckBox из XAML (уже заменён в фазе 5)

**Верификация:** нет warning-ов о неиспользуемых свойствах.

### Фаза 7: Тестирование в Revit

18. **Простая цепочка:** Dynamic → Труба → Отвод. +1, +2, Rotate, −2, −1.
19. **Ветвление:** Dynamic → Тройник → [Труба1, Труба2]. +1 (тройник), +2 (обе трубы).
20. **Каскад:** Dynamic → Тройник → Тройник → [4 отвода]. +1, +2, +3, Rotate, −3, −2, −1.
21. **Размеры:** несовпадение DN → вставка reducer. − удаляет reducer.
22. **Cancel на любой глубине:** RollBack всей TransactionGroup.
23. **Connect на глубине 2:** элементы уровней 1-2 остаются соединёнными, свободные коннекторы видны.

---

## 13. Риски и решения

### Риск 1: ConnectTo требует пространственного совпадения

**Проблема:** После перемещения Dynamic элементы цепочки остались на старых позициях. ConnectTo не сработает без перемещения.

**Решение (НОВОЕ vs v1):** Шаг c в алгоритме 5.4 — перемещение элемента к родительскому коннектору через `ConnectorAligner.ComputeAlignment` + `MoveElement` + `RotateElement` + коррекция позиции. Это тот же паттерн что используется в `Init()` и `AlignFittingToStatic`.

### Риск 2: DisconnectAll с хардкодом индекса 0

**Проблема (ИСПРАВЛЕНО vs v1):** В v1 был `DisconnectAllFromConnector(doc, elemId, 0)` — отсоединяет ТОЛЬКО коннектор с индексом 0.

**Решение:** Итерируем ВСЕ коннекторы элемента через `GetAllConnectors()` и вызываем `DisconnectAllFromConnector` для каждого несвободного.

### Риск 3: Восстановление ориентации при −

**Проблема (ИСПРАВЛЕНО vs v1):** В v1 использовался `RotationAxis + RotationAngle` — хрупкий подход с накоплением ошибок.

**Решение:** Храним полный `Transform` (Origin + BasisX/Y/Z). Восстанавливаем через `ConnectorAligner.ComputeAlignment` от текущего к целевому + финальная коррекция позиции.

### Риск 4: Восстановление соединений при −

**Проблема:** Коннекторы соседей уровня N+1 могут быть заняты.

**Решение:** Перед `ConnectTo` — проверить `IsFree`. Если занят — `DisconnectAllFromConnector` для соседа.

### Риск 5: Reducer при −

**Проблема:** Reducer может быть соединён. `doc.Delete()` бросит исключение.

**Решение:** Перед удалением — `DisconnectAllFromConnector` для всех коннекторов reducer, затем `_fittingInsertSvc.DeleteElement()`.

### Риск 6: MEPCurve при вращении

**Проблема:** `RotateElements` корректно вращает MEPCurve (Revit обновляет `Location.Curve`).

**Решение:** Snapshot сохраняет `CurveStart`/`CurveEnd`. При − восстанавливается через `Line.CreateBound()`.

### Риск 7: Производительность

**Проблема:** BFS через AllRefs + ConnectorAligner для каждого элемента.

**Решение:** Для 20-30 элементов — приемлемо. ConnectorAligner — чистая математика (O(1)). BFS — O(N). Реальное ограничение — количество `doc.Regenerate()` вызовов (один на элемент в +). При необходимости — батчить.

### Риск 8: Reducer не найден в маппинге

**Проблема:** `IFittingMapper.GetMappings()` может не вернуть правило с `ReducerFamilies`.

**Решение:** `InsertReducer` возвращает null. В алгоритме 5.4 шаг d — если reducer == null, ConnectTo всё равно вызывается (соединение с разным DN допустимо в Revit, хотя и с предупреждением). Лог: `SmartConLogger.Warn("Reducer не найден для {parentDN} → {childDN}")`.

### Риск 9: Snapshot элемента теряет актуальность

**Проблема:** Snapshot захвачен ДО +. Если между + и − была операция Rotate, элемент находится не там где был при захвате.

**Решение:** Это корректное поведение: − возвращает элемент в ИСХОДНОЕ состояние (до +), а не в промежуточное. Элемент "выпадает" из цепочки и возвращается на своё место в модели.

### Риск 10: EditFamily внутри транзакции (НОВОЕ v2.4)

**Проблема (обнаружена при реализации):** `RevitParameterResolver.GetConnectorRadiusDependencies()` для FamilyInstance с ReadOnly-параметрами вызывает `doc.EditFamily()` (строка 159). Revit **запрещает** `EditFamily` при `doc.IsModifiable=true` (внутри транзакции) → `InvalidOperationException`.

**Решение:** Код имеет guard `!doc.IsModifiable` (строка 149) — не крашится. НО без кеша теряется формульный путь → fallback на менее точный `TryChangeTypeTo`. **Фикс:** Pre-caching всех deps в `PipeConnectCommand.Execute()` ДО UI (секция 8, S6.2).

### Риск 11: Каскадный сбой подгонки размеров (НОВОЕ v2.4)

**Проблема (обнаружена при реализации):** Если `targetRadius = parentProxy.Radius` и Level 1 не смог сменить DN (остался DN65), то Level 2 видит "одинаковый DN с Level 1" → пропускает подгонку. Вся цепочка остаётся с исходным размером.

**Решение:** `targetRadius = _ctx.DynamicConnector.Radius` — всегда радиус Dynamic-коннектора. Каждый элемент подгоняется независимо, без каскадной зависимости.

### Риск 12: TrySetConnectorRadius возвращает true но размер не тот (НОВОЕ v2.4)

**Проблема (обнаружена при реализации):** `param.Set(targetValue)` может вернуть `true`, но Revit под капотом округляет/ограничивает значение (lookup-таблицы, формулы семейства). Код считал подгонку успешной, а размер остался исходным.

**Решение:** После `TrySetConnectorRadius` + `doc.Regenerate()` — ОБЯЗАТЕЛЬНО `RefreshConnector` + проверка `|actual - target| > 1e-5`. Только если фактический радиус совпал — подгонка считается успешной.

---

## 14. Сводное поведение UI

| Элемент | Поведение |
|---------|-----------|
| `[−]` | Disabled при depth = 0. Отсоединяет элементы текущего уровня, удаляет reducers, восстанавливает из snapshot. Одна транзакция. |
| `[+]` | Disabled при depth = max. Захватывает snapshot, DisconnectAll, перемещает к родителю, AdjustSize/reducer, ConnectTo. Одна транзакция. |
| Число | Текущая глубина (0..N). Read-only. |
| Hint | `"X/Y в A/B ур."` или `"нет цепочки"` |
| Rotate | Вращает dynamic + fitting + все элементы до depth + их reducers. GlobalYSnap только для dynamic. |
| Connect | Соединяет static↔fitting↔dynamic (существующая логика). Элементы цепочки уже подключены через +. |
| Cancel | `_groupSession.RollBack()` — откат ВСЕЙ TransactionGroup (все +, −, Rotate, Size). |

---

## 15. Гарантии

1. **Атомарность уровня:** Каждый +/− — одна транзакция для ВСЕХ элементов уровня.
2. **Полный откат:** Cancel отменяет ВСЕ транзакции (TransactionGroup.RollBack).
3. **Коррекция углов:** GlobalYSnap только для dynamic после вращения. Элементы цепочки — rigid body.
4. **Snapshot ДО модификации:** Захватывается перед +, восстанавливается при −. Элемент возвращается в исходное состояние.
5. **Идемпотентность:** Повторный + на том же уровне невозможен (CanIncrementChain = false).
6. **Нет хардкода DN:** Размеры определяются через `Connector.Radius` (universal) и `IParameterResolver`.
7. **Нет утечки элементов:** Reducers отслеживаются в `InsertedReducerIds`, удаляются при −, откатываются при Cancel.

---

## 16. Открытые вопросы

1. **Reducer не найден:** Если `IFittingMapper` не вернул правило с ReducerFamilies — допускаем ConnectTo с разным DN (Revit покажет warning) или блокируем +? **Рекомендация:** допускаем + лог.
2. **MEPSystem:** Сейчас план только для FamilyInstance и MEPCurve. MEPSystem (воздуховоды, кабельные лотки) — аналогичная логика, но другие `BuiltInParameter`. **Рекомендация:** defer на Phase 8.
3. **Глубина > 5 уровней:** Производительность при 50+ элементах не тестирована. **Рекомендация:** ограничить MaxLevel = 10 в UI (configurable).
4. **Undo после Connect:** После Assimilate вся операция — одна запись Undo. Нет гранулярного отката уровней после Connect. **Рекомендация:** это ожидаемое поведение (ADR-003).

---

## 17. Changelog v1 → v2

| # | Что изменено | Причина |
|---|-------------|---------|
| 1 | Добавлена полная инвентаризация существующего кода (секция 1.1) | Агент-исполнитель должен знать что уже есть |
| 2 | `ElementChainIterator` — явно помечен как НЕСУЩЕСТВУЮЩАЯ реализация | v1 путал "новый"/"уже существует" |
| 3 | Snapshot: полный Transform вместо RotationAxis+RotationAngle | axis+angle хрупок, накапливает ошибки |
| 4 | Алгоритм +: добавлен шаг ПЕРЕМЕЩЕНИЯ элементов через ConnectorAligner | v1 пропустил — ConnectTo требует spatial proximity |
| 5 | DisconnectAll: итерация по ВСЕМ коннекторам вместо хардкода индекса 0 | v1: `DisconnectAllFromConnector(doc, elemId, 0)` — отсоединяет только один |
| 6 | GetCurrentDn: исправлена формула (`* 2` для radius→diameter) | v1 секция 7.3 содержала `/ 2` (radius→ещё меньше) |
| 7 | `INetworkSizeAdjuster` → `INetworkMover` | Более точное название, AdjustSize делается через IParameterResolver напрямую |
| 8 | `NetworkSnapshotStore` вынесен из контекста в ViewModel | Мутабельный объект не должен быть в immutable контексте |
| 9 | InsertReducer полностью определён (алгоритм 7.2) | v1 ссылался на InsertReducer без определения |
| 10 | Таблица существующих сервисов (секция 7.3) | Агент должен знать что переиспользовать |
| 11 | Добавлены конкретные CanExecute для команд | v1 не определял логику enabled/disabled |
| 12 | Snapshot захватывается ДО +, а не ПОСЛЕ | v1 противоречил себе ("исходная позиция" vs "после последнего +") |

### v2.1 — исправления по рецензии

| # | Что изменено | Причина |
|---|-------------|---------|
| 13 | `ElementIdEqualityComparer` → `public` (секции 4.1, 9, 11) | Был `internal` — недоступен из `PipeConnectCommand` (другая сборка) |
| 14 | Добавлено определение `IsInCurrentChain` (секция 5.5) | Метод использовался но не был определён |
| 15 | Полная сигнатура конструктора ViewModel (секция 8) | Агент должен видеть все параметры и новые поля |
| 16 | `BoolToVisibilityConverter` — регистрация в XAML (секция 6.1) | Конвертер существует в `SmartCon.UI` но не зарегистрирован как StaticResource |
| 17 | `MaxChainLevel = 10` в `CanIncrementChain` (секция 6.3) | Ограничение глубины для производительности (из вопроса 16.3) |
| 18 | Комментарий про `RotationCenter` в алгоритме 5.5 | Уточнение: после `lp.Point = snapshot.FiOrigin` центр вращения корректен |

### v2.2 — исправления по второй рецензии

| # | Что изменено | Причина |
|---|-------------|---------|
| 19 | **КРИТИЧЕСКОЕ:** ConnectTo через reducer (алгоритм 5.4 шаги d+e) | v2 соединял parent↔child напрямую, даже если reducer вставлен. Нужно parent↔reducer↔child + re-align child к reducer conn2 |
| 20 | Секция 3.4 — snapshot ДО + (не ПОСЛЕ) | Противоречие: 3.4 говорил "после +", алгоритм 5.4 — "до +". Исправлено единообразно |
| 21 | Секция 2.4 — формулировка snapshot поведения | Было "возвращается к состоянию после последнего +", стало "возвращается в исходное положение в модели" |
| 22 | Секция 3.7 — GlobalYSnap: только dynamic, это НОВЫЙ код | Было "существующий код строки 261-296" — они в Init(), не в ExecuteRotate. Нужно ДОБАВИТЬ |
| 23 | Алгоритм 5.6 шаг 5 — полный код GlobalYSnap | Было "существующий код", стало полный псевдокод по аналогии с Init |
| 24 | `IsInCurrentChain` + `FindEdgeToParent` — `ElementIdEqualityComparer` | `List.Contains` использует reference equality для ElementId |
| 25 | Refresh после AdjustSize в шаге d | DN мог измениться после TrySetConnectorRadius — нужен refresh перед InsertReducer |
| 26 | `MaxChainLevel` 10 → 20 → 10 | 20-30 элементов ≈ 4-5 уровней BFS, MaxChainLevel=10 с запасом |
| 27 | Отклонено: `_groupSession` vs `_txService` | Рецензент ошибся: ViewModel ИМЕЕТ `_groupSession` (строка 31), все операции используют его |

### v2.3 — исправления по третьей рецензии

| # | Что изменено | Причина |
|---|-------------|---------|
| 28 | `InsertReducer` шаг 1: `GetMappings(parent.CTC, parent.CTC)` вместо `(parent, child)` | Reducer соединяет ОДИН тип коннектора (Pipe↔Pipe). В ViewModel строка 98: `rule.FromType.Value == rule.ToType.Value` |
| 29 | `AlignFittingToStatic` — комментарий про возврат conn2 | Метод возвращает ВТОРОЙ коннектор (смотрящий на child) — именно тот что нужен |
| 30 | `PipeConnectCommand` — комментарий про контекст ExternalEvent | Все `ServiceHost.GetService` вызываются в `Execute()` ДО `ExternalEventActionQueue` |
| 31 | `MaxChainLevel` 20 → 10 | 30 элементов ≈ 4-5 уровней BFS, 10 достаточно с запасом |

### v2.4 — исправления по результатам первой реализации

> Источник: анализ реальных багов при попытке реализации плана v2.3.
> Все 7 пунктов верифицированы против кодовой базы.

| # | Что изменено | Причина |
|---|-------------|---------|
| 32 | **КРИТИЧЕСКОЕ:** Секция 3.5 полностью переписана — единый подход к размерам | 3 ошибки: MEPCurve пропускал TrySetConnectorRadius; targetRadius из parent вместо Dynamic; только 1 коннектор у многопортовых фитингов |
| 33 | **КРИТИЧЕСКОЕ:** Алгоритм 5.4 шаг d полностью переписан | `targetRadius = DynamicConnector.Radius`, вызов для ВСЕХ типов, многопортовые фитинги, верификация фактического радиуса |
| 34 | **КРИТИЧЕСКОЕ:** Добавлен Pre-caching (секция 8, S6.2) | `GetConnectorRadiusDependencies` вызывает `EditFamily` для ReadOnly-параметров → ЗАПРЕЩЕНО внутри транзакции. Прогрев кеша ДО UI |
| 35 | **КРИТИЧЕСКОЕ:** Секция 7.4 — рефакторинг `TrySetConnectorRadius` | `dep.IsInstance=true` → `return false` без попытки `TryChangeTypeTo`. Нужен fallback для фитингов с lookup-таблицами |
| 36 | `MaxChainLevel` 10 → 30 | Линейная сеть: 10 труб + 10 отводов = 20 уровней BFS. 30 с запасом |
| 37 | `BoolToVisibilityConverter` — локальный в `SmartCon.PipeConnect` | Проблемы с подтягиванием из другой сборки через XAML |
