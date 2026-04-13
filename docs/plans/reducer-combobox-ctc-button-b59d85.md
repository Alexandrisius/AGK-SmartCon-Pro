# Виртуальный CTC + Reducer ComboBox + кнопка смены CTC

CTC хранится в виртуальном store; `LoadFamily` (запись в семейство) вызывается **только один раз** на «Соединить». `EditFamily` (чтение) разрешён везде — он быстрый.

---

## Ключевая идея: Virtual CTC

**Проблема:** каждое назначение CTC = `EditFamily` → Transaction → `LoadFamily` ≈ 1-3 сек. Сейчас это происходит ДО вставки фитинга — пользователь ждёт и не видит результата.

**Разделение операций:**
- `EditFamily` (**чтение**) — быстро, разрешено везде (таблицы поиска, формулы, имена параметров)
- `LoadFamily` (**запись**) — тяжёлая, откладывается ИСКЛЮЧИТЕЛЬНО на `Connect()`

**Решение:** CTC хранится в `VirtualCtcStore`. Алгоритмы (alignment, sizing) работают с виртуальными значениями. `LoadFamily` — **однократно** в `Connect()`.

### Где CTC сейчас (и что меняем)

| Место | Сейчас | С Virtual CTC |
|---|---|---|
| `AlignFittingToStatic()` строка 63 | `connector.Description` | Новый параметр `ctcOverrides` |
| `EnsureFittingCtcForInsert()` | EditFamily(r) + dialog + `LoadFamily` | **Заменить на** `GuessCtcForFitting` (без dialog, без LoadFamily) |
| `EnsureReducerCtcForInsert()` | EditFamily(r) + dialog + `LoadFamily` | **Заменить на** `GuessCtcForReducer` (без dialog, без LoadFamily) |
| `IsFittingCtcDefined()` | EditFamily(r) | **Убрать** — виртуальный CTC всегда есть |
| `BuildConnectorItems()` | EditFamily(r) | **Оставить** — используется только по кнопке ⚙ |
| `ApplyFittingCtcToFamily()` | EditFamily(rw) + `LoadFamily` | **Отложить** на `Connect()` |
| S1.1/S2.1 `EnsureTypeCode` FamilyInstance | `SetConnectorTypeCode` → EditFamily + `LoadFamily` | Виртуальный CTC → `LoadFamily` в `Connect()` |
| S1.1/S2.1 `EnsureTypeCode` MEPCurve | Параметр типоразмера | Без изменений (быстро) |

---

## План реализации

### Шаг 1. VirtualCtcStore

**Новый файл:** `SmartCon.Core/Models/VirtualCtcStore.cs`

```csharp
public sealed class VirtualCtcStore
{
    // (ElementId.Value, ConnectorIndex) → CTC
    private readonly Dictionary<(long, int), ConnectionTypeCode> _overrides = new();
    
    // (ElementId.Value, ConnectorIndex) → полный ConnectorTypeDefinition для отложенной записи
    private readonly Dictionary<(long, int), ConnectorTypeDefinition> _pendingWrites = new();

    public void Set(ElementId elemId, int connIdx, ConnectionTypeCode ctc, ConnectorTypeDefinition? typeDef = null);
    public ConnectionTypeCode? Get(ElementId elemId, int connIdx);
    public IReadOnlyDictionary<int, ConnectionTypeCode> GetOverridesForElement(ElementId elemId);
    public IReadOnlyList<(ElementId, int, ConnectorTypeDefinition)> GetPendingWrites();
    public void Clear();
}
```

**Где создаётся:** в `PipeConnectCommand.Execute()`, до `EnsureTypeCode`.  
**Как передаётся:** через `PipeConnectSessionContext.VirtualCtcStore` → доступен и в Command, и в ViewModel.

### Шаг 2. S1.1/S2.1 — виртуальный CTC для static/dynamic

**Файл:** `PipeConnectCommand.cs` — метод `EnsureTypeCode`

**Было (FamilyInstance):**
```
MiniTypeSelector → familyConnSvc.SetConnectorTypeCode() → LoadFamily → RefreshConnector
```

**Стало (FamilyInstance):**
```
MiniTypeSelector → virtualCtcStore.Set(elemId, connIdx, ctc, selected) → НЕТ LoadFamily
```

**Критически важно:** после `EnsureTypeCode`, `PipeConnectCommand` делает:
```csharp
dynamicProxy = connectorSvc.GetNearestFreeConnector(
    doc, dynamicProxy.OwnerElementId, dynamicProxy.Origin) ?? dynamicProxy;
```
Это перечитывает proxy из Revit → **теряет** виртуальный CTC. **Фикс:**
```csharp
dynamicProxy = dynamicProxy with { ConnectionTypeCode = new ConnectionTypeCode(result.Code) };
```
после `RefreshConnector`. Аналогично для `staticProxy`.

**Для MEPCurve/FlexPipe:** оставить текущую запись (быстро, запись в параметр типоразмера в транзакции).

### Шаг 3. Автоугадывание CTC для фитингов

**Файл:** `PipeConnectEditorViewModel.cs` — новый метод `GuessCtcForFitting`

**Принцип:** ищем **прямые коннекты** (IsDirectConnect=true) в таблице маппинга.

**Алгоритм для адаптера:**
1. Знаем `staticCTC` и `dynamicCTC` (из `_ctx.StaticConnector`/`_ctx.DynamicConnector`, уже с виртуальным CTC)
2. Поиск: какой CTC имеет прямой коннект с staticCTC?
   `rules.Where(r => r.IsDirectConnect && (r.FromType == staticCTC || r.ToType == staticCTC))`
   → `counterpartForStatic` (напр., НР для ВР)
3. Аналогично для dynamicCTC → `counterpartForDynamic` (напр., гладкий конец ПП для муфты ПП)
4. **50/50:** не знаем какой физический коннектор — какой →
   connector 0 → counterpartForStatic, connector 1 → counterpartForDynamic
5. `virtualCtcStore.Set(fittingId, connIdx, guessedCtc)`
6. Вернуть `GetOverridesForElement(fittingId)` для передачи в `AlignFittingToStatic`

**Алгоритм для reducer (`GuessCtcForReducer`):**
- staticCTC == dynamicCTC → оба коннектора = тот же CTC
- staticCTC ≠ dynamicCTC → **реверс**: conn к static = dynamicCTC, conn к dynamic = staticCTC

**Пример (адаптер):**
```
static=ВР(2), dynamic=муфтаПП(5)
Маппинг: ВР(2)↔НР(3) direct, муфтаПП(5)↔гладкийПП(6) direct
→ фитинг: conn0=НР(3), conn1=гладкийПП(6)
AlignFittingToStatic с ctcOverrides → Стратегии 0/1/2 работают → правильная ориентация
```

### Шаг 4. Рефакторинг InsertFittingSilent

**Файл:** `PipeConnectEditorViewModel.cs`

**Было:**
```
EnsureFittingCtcForInsert(fitting) → dialog + LoadFamily
InsertFitting → Regenerate
AlignFittingToStatic(... dynamicTypeCode) → читает connector.Description
SizeFittingConnectors
```

**Стало:**
```
InsertFitting → Regenerate
GuessCtcForFitting(insertedId, rule) → виртуальные CTC, НЕТ LoadFamily
AlignFittingToStatic(... ctcOverrides: overrides) → использует виртуальные CTC
SizeFittingConnectors
```

`EnsureFittingCtcForInsert` и `EnsureReducerCtcForInsert` **больше не вызываются**. Могут быть удалены или оставлены как dead code.

### Шаг 5. AlignFittingToStatic — параметр ctcOverrides

**Файлы:** `IFittingInsertService.cs`, `RevitFittingInsertService.cs`

```csharp
ConnectorProxy? AlignFittingToStatic(
    Document doc, ElementId fittingId, ConnectorProxy staticProxy,
    ITransformService transformSvc, IConnectorService connSvc,
    ConnectionTypeCode dynamicTypeCode = default,
    IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null);  // NEW
```

В реализации (строка 60-66):
```csharp
var ctc = ctcOverrides != null && ctcOverrides.TryGetValue((int)c.Id, out var ovr)
    ? ovr
    : ConnectionTypeCode.Parse(GetConnectorDescriptionSafe(c));
```

### Шаг 6. Разделить AvailableFittings и AvailableReducers

**Файл:** `PipeConnectEditorViewModel.cs`

- `ObservableCollection<FittingCardItem> AvailableReducers { get; }` — новая коллекция
- `[ObservableProperty] FittingCardItem? _selectedReducer`
- `[ObservableProperty] bool _isReducerVisible` — видимость секции
- Конструктор: reducer → `AvailableReducers`, фитинги → `AvailableFittings`

### Шаг 7. Вставка reducer ДО окна

**Файл:** `PipeConnectEditorViewModel.cs`

**Текущая проблема:** `_needsPrimaryReducer` определяется в `ValidateAndFixBeforeConnect()` (внутри `Connect()`). Reducer вставляется в `Connect()` → пользователь видит его только после закрытия окна.

**Решение:** детекция и вставка reducer в `Init()` и `ChangeDynamicSize()`.

**В `Init()` — после прямого sizing (строки 510-555):**
```
1. Обновить _activeDynamic
2. Проверить: |_activeDynamic.Radius - staticRadius| > eps?
3. Если да → _needsPrimaryReducer = true
4. InsertReducerSilent(SelectedReducer) → GuessCtcForReducer → Align + Size
5. IsReducerVisible = true
```

**В `ChangeDynamicSize()` — после обновления фитинга:**
- Радиусы ≠ → вставить reducer + показать ComboBox
- Радиусы == → удалить reducer + скрыть ComboBox

**Из `Connect()` удалить** блок отложенной вставки reducer (он уже вставлен).

### Шаг 8. RefreshWithCtcOverride

**Файл:** `PipeConnectEditorViewModel.cs`

```csharp
private ConnectorProxy? RefreshWithCtcOverride(Document doc, ElementId elemId, int connIdx)
{
    var proxy = _connSvc.RefreshConnector(doc, elemId, connIdx);
    if (proxy is null) return null;
    var ctc = _virtualCtcStore.Get(elemId, connIdx);
    return ctc.HasValue ? proxy with { ConnectionTypeCode = ctc.Value } : proxy;
}
```

**Где заменить `_connSvc.RefreshConnector` → `RefreshWithCtcOverride`:**
- `Init()` строка 495: `_activeDynamic = ...` — для dynamic FamilyInstance
- `ChangeDynamicSize()`: после `_connSvc.RefreshConnector(doc, dynId, dynIdx)`
- Все места где `_activeDynamic` пересоздаётся из static/dynamic elements
- **НЕ нужно** для фитинга/reducer (там CTC используется через `ctcOverrides` в `AlignFittingToStatic`)

### Шаг 9. Кнопка ⚙ (ReassignCtc) — без LoadFamily

**Файл:** `PipeConnectEditorViewModel.cs`

`[RelayCommand] ReassignFittingCtc` / `[RelayCommand] ReassignReducerCtc`:
1. `BuildConnectorItems(symbol, types, rule)` — **существующий метод** (EditFamily read — быстро)
   - Модификация: предзаполнять `SelectedType` из `virtualCtcStore` вместо чтения Description
2. `ShowFittingCtcSetup` с items
3. Если Ok → обновить `virtualCtcStore` → перевставить фитинг/reducer с новыми `ctcOverrides`
4. Если Cancel → ничего не менять, оставить предыдущие виртуальные CTC

### Шаг 10. Команда InsertReducer

**Файл:** `PipeConnectEditorViewModel.cs`

`[RelayCommand] InsertReducerCommand` (аналог InsertFitting):
- Удалить текущий reducer если есть
- Вставить `SelectedReducer`
- `GuessCtcForReducer` → overrides
- `AlignFittingToStatic` с ctcOverrides → Size

### Шаг 11. XAML — Reducer ComboBox + кнопки ⚙

**Файл:** `PipeConnectEditorView.xaml`

```
Секция фитинга (Grid.Row="8"):
[⚙] [ComboBox фитингов           ] [Вставить]

Секция reducer (видимость по IsReducerVisible):
[⚙] [ComboBox reducer-ов         ] [Вставить]

(кнопки +/−/Подключить всё сдвигаются ниже, высота окна не меняется)
```

### Шаг 12. FlushVirtualCtcToFamilies — запись на Connect()

**Файл:** `PipeConnectEditorViewModel.cs` — метод `Connect()`

**Новая зависимость:** `IFamilyConnectorService` добавить в конструктор ViewModel.

**Новый метод `FlushVirtualCtcToFamilies()`:**
1. Берёт `_virtualCtcStore.GetPendingWrites()` — все отложенные записи
2. **Фитинги/Reducer:** группирует по FamilySymbol → `ApplyFittingCtcToFamily()` (EditFamily + LoadFamily)
3. **Static/Dynamic FamilyInstance:** `_familyConnSvc.SetConnectorTypeCode()` (EditFamily + LoadFamily)
4. `doc.Regenerate()`

**Порядок в Connect():**
```
FlushVirtualCtcToFamilies() → Regenerate → ValidateAndFix → ConnectTo → Assimilate
```

### Шаг 13. Строгая валидация

**В PipeConnectCommand (до Init):**
- S1.1/S2.1: если MiniTypeSelector отменён → `return Result.Cancelled`

**В Connect() (перед Assimilate):**
1. Размеры всех пар совпадают (± 1e-5 ft)
2. CTC из virtualCtcStore соответствуют таблице маппинга
3. Позиции коннекторов совмещены (ε ≈ 0.1 мм)
4. Если не сходится → StatusMessage, **не** ассимилировать

### Шаг 14. NetworkMover — поддержка ctcOverrides

**Файлы:** `INetworkMover.cs`, `NetworkMover.cs`

```csharp
ElementId? InsertReducer(Document doc,
    ConnectorProxy parentConn, ConnectorProxy childConn,
    IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null);
```

---

## Затрагиваемые файлы

| Файл | Изменение |
|---|---|
| `SmartCon.Core/Models/VirtualCtcStore.cs` | **Новый** |
| `SmartCon.Core/Models/PipeConnectSessionContext.cs` | + `VirtualCtcStore` |
| `SmartCon.Core/Services/Interfaces/IFittingInsertService.cs` | + `ctcOverrides` |
| `SmartCon.Core/Services/Interfaces/INetworkMover.cs` | + `ctcOverrides` |
| `SmartCon.PipeConnect/Commands/PipeConnectCommand.cs` | Виртуальный CTC в S1.1/S2.1 |
| `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` | Основные изменения (≈300-400 строк) |
| `SmartCon.PipeConnect/Views/PipeConnectEditorView.xaml` | + Reducer ComboBox + кнопки ⚙ |
| `SmartCon.Revit/Fittings/RevitFittingInsertService.cs` | + `ctcOverrides` в AlignFittingToStatic |
| `SmartCon.Revit/Network/NetworkMover.cs` | + `ctcOverrides` проброс |

---

## Выигрыш

- **До:** несколько `LoadFamily` на каждую вставку фитинга + S1.1/S2.1 — задержка на каждом шаге
- **После:** 0× `LoadFamily` до «Соединить»; `EditFamily` (read) разрешён (быстро)
- Пользователь видит собранный узел мгновенно, корректирует CTC кнопкой ⚙ если нужно

## Риски

1. **50/50 ориентация** — типы CTC определяются точно (из маппинга), но назначение conn 0 vs conn 1 может быть перепутано → кнопка ⚙
2. **RefreshConnector теряет CTC** — решено: `RefreshWithCtcOverride` для static/dynamic; `ctcOverrides` параметр для фитингов
3. **LoadFamily в TransactionGroup** — Revit создаёт внутреннюю транзакцию → `RollBack()` откатит всё
4. **Несколько direct-connect правил** — если для одного CTC есть 2+ прямых коннекта, берём первый. Пользователь может поправить через ⚙
5. **IFamilyConnectorService в ViewModel** — +1 зависимость в конструкторе, нужно обновить DI и PipeConnectCommand
