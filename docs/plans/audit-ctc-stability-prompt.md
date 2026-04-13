# Промпт для глубокого аудита стабильности CTC-механизма SmartCon

Скопируй всё содержимое ниже и отправь в новую сессию opencode.

---

## Контекст

Ты работаешь над проектом **SmartCon** — плагином для Autodesk Revit 2025 (.NET 8 / C# 12 / WPF). Флагманский модуль — **PipeConnect**: соединение трубных MEP-элементов в 3D двумя кликами.

**Solution:** `src/SmartCon.sln`
**Документация:** `docs/` (SSOT — `docs/README.md`, `docs/invariants.md`)
**Инструкции:** `AGENTS.md` в корне проекта — загрузи и следуй.

## Проблема

Был реализован глобальный план «Virtual CTC + Reducer ComboBox» (см. `docs/plans/reducer-combobox-ctc-button-b59d85.md`). План включал:
- VirtualCtcStore — хранение CTC в памяти вместо немедленной записи в семейство
- GuessCtcForFitting/GuessCtcForReducer — автоугадывание типов коннекторов фитингов
- FlushVirtualCtcToFamilies — отложенная запись в семейство через EditFamily+LoadFamily
- PromoteGuessedCtcToPendingWrites — промоут угаданных CTC в pending writes

После реализации появилось **множество багов**, связанных с путаницей индексов коннекторов и записью CTC на неправильные коннекторы. Часть уже исправлена, но стабильность пошатнулась и нужна **полная ревизия**.

## Уже найденные и исправленные баги

1. **Spatial map → плоский список** (`ApplyFittingCtcToFamily`): `MatchItemsToConnElems` возвращал плоский список вместо маппинга `connElemIndex → item`. CTC записывался на неправильный коннектор семейства. **Исправлено:** `BuildSpatialCtcMap` возвращает `Dictionary<int, FittingCtcSetupItem>`.
2. **PromoteElementCtcToPendingWrites пропускал элементы** с уже записанным (но неправильным) CTC. **Исправлено:** сравнение виртуальных CTC с реальными.
3. **Радиусная эвристика в GuessCtcForReducer** была хрупкой. **Исправлено:** spatial matching по расстоянию до staticProxy.
4. **GuessReducerCtc fallback** мог назначить оба CTC одинаковыми. **Исправлено:** гранулярный fallback.

---

## Архитектурная карта CTC-потока (ОБЯЗАТЕЛЬНО изучить перед аудитом)

```
PipeConnectCommand.Execute()                              ← строка 48-75
  │
  ├── new VirtualCtcStore()                               ← строка 75
  │
  ├── S1.1: EnsureTypeCode(dynamic)
  │     └── FamilyInstance → virtualCtcStore.Set(elemId, connIdx, ctc, typeDef)
  │         MEPCurve → IFamilyConnectorService.SetConnectorTypeCode (сразу)
  │
  ├── S2.1: EnsureTypeCode(static)
  │     └── Аналогично
  │
  ├── sessionCtx.VirtualCtcStore = virtualCtcStore        ← строка 163
  │
  └── PipeConnectEditorViewModel.Init()
        ├── RefreshWithCtcOverride(doc, elemId, connIdx)  ← строка 1889
        │     └── RefreshConnector + подмена CTC из store
        │
        ├── InsertFittingSilent(fitting)                  ← строка 2402
        │     ├── GuessCtcForFitting(id, rule)            ← строка 1901
        │     │     ├── allDefined → store.Set (no typeDef)
        │     │     └── guess → spatial matching → store.Set (no typeDef)
        │     ├── AlignFittingToStatic(... ctcOverrides)  ← RevitFittingInsertService:36
        │     └── SizeFittingConnectors(...)
        │
        └── InsertReducerSilent()                         ← строка 2115
              ├── GuessCtcForReducer(id)                  ← строка 1945
              ├── AlignFittingToStatic(... ctcOverrides)
              └── SizeFittingConnectors(...)

  ViewModel.Connect()                                     ← строка 2565
    ├── ValidateAndFixBeforeConnect()                     ← строка 2705
    ├── Insert reducer if needed                          ← строка 2576
    ├── PromoteGuessedCtcToPendingWrites()                ← строка 2028
    │     └── PromoteElementCtcToPendingWrites(elemId)    ← строка 2034
    │           └── if allDefined && allMatch → skip
    │           └── else → store.Set(elemId, connIdx, ctc, typeDef)
    ├── FlushVirtualCtcToFamilies()                       ← строка 3648
    │     ├── FamilyInstance → ApplyFittingCtcToFamily    ← строка 3392
    │     │     ├── BuildSpatialCtcMap (spatial matching) ← строка 3547
    │     │     ├── Order matching (by ElementId sort)    ← строка 3460
    │     │     └── Positional fallback                   ← строка 3508
    │     └── MEPCurve → IFamilyConnectorService
    ├── ConnectTo (Revit API)
    └── Assimilate()
```

## Ключевые типы и их значения (шпаргалка)

| Выражение | Тип | Значение |
|---|---|---|
| `Connector.Id` (Revit API) | `int` | Уникальный ID коннектора в рамках элемента. **НЕ порядковый индекс!** Может быть 1, 2, 3 или любым другим int |
| `ConnectorProxy.ConnectorIndex` | `int` | = `(int)connector.Id` — устанавливается в `ConnectorWrapper.ToProxy()` (строка 26) |
| `ctcOverrides` ключи | `int` | = `ConnectorProxy.ConnectorIndex` = `(int)connector.Id` |
| `AlignFittingToStatic` lookup | `(int)c.Id` | `c` это Revit `Connector`, `c.Id` это тот же `Connector.Id` |
| `BuildSpatialCtcMap` ключ результата | `int` | = индекс в `connElems` (порядок `FilteredElementCollector`) |
| `connElems` порядок | — | `FilteredElementCollector` по `OST_ConnectorElem` — НЕ гарантирован, но стабилен в рамках одного family doc |
| `FittingCtcSetupItem.ConnectorIndex` | `int` | = `(int)connector.Id` из project-level коннектора |
| `VirtualCtcStore` ключ | `(long, int)` | = `(ElementId.Value, ConnectorIndex)` |

## Файлы для аудита

| Файл | Строк | Что проверять |
|---|---|---|
| `src/SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` | 3718 | Все CTC-методы (список ниже) |
| `src/SmartCon.Revit/Fittings/RevitFittingInsertService.cs` | 254 | AlignFittingToStatic: стратегии 0-4, ctcOverrides lookup |
| `src/SmartCon.Revit/Wrappers/ConnectorWrapper.cs` | 67 | ToProxy: ConnectorIndex = (int)connector.Id |
| `src/SmartCon.Core/Models/VirtualCtcStore.cs` | 122 | Set/Get/GetOverridesForElement/GetPendingWrites/TransferOverrides |
| `src/SmartCon.Core/Models/CtcGuesser.cs` | 97 | GuessAdapterCtc, GuessReducerCtc, FindDirectConnectCounterpart, CanDirectConnect |
| `src/SmartCon.Core/Models/ConnectorProxy.cs` | 34 | Record с ConnectorIndex |
| `src/SmartCon.Core/Models/FittingCtcSetupItem.cs` | 27 | ConnectorIndex, SelectedType |
| `src/SmartCon.PipeConnect/Commands/PipeConnectCommand.cs` | 482 | EnsureTypeCode, IsKnownTypeCode, virtualCtcStore.Set |

---

## Твоя задача: ГЛУБОКИЙ аудит стабильности

Пройди **каждый** из перечисленных ниже сценариев. Для каждого:
1. Прочитай код метода **полностью** — не только сигнатуру, а каждую строку тела
2. Трассируй значения ключей: откуда берётся ключ, какой тип он имеет, куда передаётся
3. Проверь что данные **не теряются** между шагами (store.Set → store.Get → overrides → AlignFitting → Connect)
4. Проверь edge cases (см. блок «Граничные условия» после сценариев)
5. Если найдёшь баг — опиши проблему, укажи файл:строка, предложи исправление, затем **исправь**

---

## Сценарий 1: Прямое соединение двух кранов (без фитинга)

**Путь:** PipeConnectCommand → S1.1 (EnsureTypeCode) → Init() → Connect() → ConnectTo

**Трассировка:**
1. `EnsureTypeCode` (PipeConnectCommand.cs:207): для FamilyInstance вызывает `virtualCtcStore.Set(proxy.OwnerElementId, proxy.ConnectorIndex, ctc, selected)` — `proxy.ConnectorIndex = (int)connector.Id` — создаёт pending write
2. После EnsureTypeCode: `staticProxy = staticProxy with { ConnectionTypeCode = new ConnectionTypeCode(result.Code) }` — proxy обновлён, но store жив
3. Init(): `RefreshWithCtcOverride(doc, elemId, connIdx)` — RefreshConnector + подмена из store
4. Connect(): прямое соединение без фитинга → `ConnectTo(static.Owner, static.ConnIdx, dynamic.Owner, dynamic.ConnIdx)`

**Проверить:**
- [ ] `proxy.ConnectorIndex` в EnsureTypeCode = `(int)connector.Id` — это корневой ключ всего потока
- [ ] После `RefreshWithCtcOverride` виртуальный CTC **не теряется** (store.Get возвращает правильное значение)
- [ ] В Connect() при прямом соединении (строка 2667-2674): `ResolveConnectorSidesForElement` **НЕ вызывается** — ConnectTo получает `staticProxy.ConnectorIndex` и `dynR.ConnectorIndex` напрямую — это корректно
- [ ] `PromoteGuessedCtcToPendingWrites()` → `PromoteElementCtcToPendingWrites(null)` для fittingId/reducerId = null → skip — это OK
- [ ] FlushVirtualCtcToFamilies: pending writes для static/dynamic (если FamilyInstance) → `ApplyFittingCtcToFamily` с 1 item, но `connElems.Count` может быть 2+ — spatial match найдёт только 1 → `result.Count(1) != items.Count(1)` → **возвращает null → fallback!** — проверить что fallback не затирает второй коннектор

**Критический вопрос:** Если у static-крана 2 коннектора (1 к dynamic, 1 свободный), Flush пишет CTC только на 1 коннектор через order/positional fallback — **не затирает ли второй?**

---

## Сценарий 2: Соединение через фитинг (адаптер/муфта)

**Путь:** Init() → InsertFittingSilent → GuessCtcForFitting → AlignFittingToStatic → SizeFittingConnectors → Connect()

**Трассировка:**
1. `InsertFittingSilent` (строка 2402): удаляет старый фитинг, вставляет новый
2. `GuessCtcForFitting(insertedId, rule)` (строка 1901):
   - Если `allDefined` (все коннекторы фитинга уже имеют CTC в Description) → store.Set **БЕЗ typeDef** → **pending write НЕ создаётся** → Flush **НЕ запишет** в семейство
   - Иначе → spatial matching → `CtcGuesser.GuessAdapterCtc(staticCTC, dynamicCTC, rules)` → store.Set **БЕЗ typeDef**
3. `AlignFittingToStatic` (RevitFittingInsertService:36):
   - `ctcOverrides.TryGetValue((int)c.Id, out var ovr)` — ключ = `Connector.Id`
   - Стратегии 0→0→1→2→3→4 определяют fitConn1 и fitConn2
   - fitConn1 выравнивается к static, fitConn2 возвращается
4. `SizeFittingConnectors` — подгонка размеров
5. `Connect()` → `ResolveConnectorSidesForElement(fittingId, fConns, dynCtc)` (строка 2077) → `GetEffectiveConnectorCtc` → `ConnectTo`

**Проверить:**
- [ ] `GuessCtcForFitting` → `conns = _connSvc.GetAllConnectors(_doc, fittingId)` — сразу после Insert+Regenerate. Коннекторы свежие. `c.ConnectorIndex = (int)c.Id` — корректно
- [ ] Spatial matching в GuessCtcForFitting: `connForStatic = conns.OrderBy(c => c.Origin.DistanceTo(staticOrigin)).First()` — фитинг ещё НЕ выровнен, коннекторы в позиции вставки (staticOrigin). **Оба коннектора могут быть равноудалены!** — проверить что First() детерминирован
- [ ] `connForDynamic = conns.First(c => c.ConnectorIndex != connForStatic.ConnectorIndex)` — работает для 2 коннекторов, но если 3+ — возьмёт только первого с другим индексом
- [ ] `counterpartForStatic`/`counterpartForDynamic` из `CtcGuesser.GuessAdapterCtc` — может вернуть `Undefined` при пустых rules. Тогда store.Set запишет `Undefined(0)` → это IsDefined=false → AlignFittingToStatic стратегии будут работать с CTC=0
- [ ] В AlignFittingToStatic: `ctcOverrides.TryGetValue((int)c.Id, ...)` — ключ в ctcOverrides = `ConnectorProxy.ConnectorIndex` = `(int)connector.Id`. Но `c` здесь — это Revit `Connector` из `ConnectorManager.Connectors`. **`c.Id` из ConnectorManager == `connector.Id` из `_connSvc.GetAllConnectors`?** — traceroute: `_connSvc.GetAllConnectors` → `ConnectorWrapper.ToProxy` → `connector.Id`. AlignFittingToStatic → `fitting.GetConnectorManager().Connectors.Cast<Connector>()` → `c.Id`. **Это один и тот же Connector.Id.** OK
- [ ] После `AlignFittingToStatic` → `RefreshConnector(doc, fittingId, fitConn2.ToProxy()?.ConnectorIndex ?? -1)` — RefreshConnector получает `(int)connector.Id` из ToProxy — совпадает с store ключами
- [ ] `PromoteGuessedCtcToPendingWrites` → для fitting: overrides есть (guessed), but **no typeDef was passed in store.Set** → `PromoteElementCtcToPendingWrites` → `FindTypeDef(ctc)` → если typeDef найден → `store.Set(elemId, connIdx, ctc, typeDef)` → создаёт pending write. **Если typeDef НЕ найден (код отсутствует в репозитории) — pending write НЕ создаётся → CTC НЕ записывается в семейство!**
- [ ] В Connect() → `ResolveConnectorSidesForElement` → `GetEffectiveConnectorCtc` читает из store по `conn.ConnectorIndex`. Если после SizeFittingConnectors/Realign коннектор пересоздался (другой ConnectorIndex) → store.Get вернёт null → fallback на `conn.ConnectionTypeCode` из Description → **если Description ещё не записан (Flush ещё не был) → CTC=Undefined**

**Критический вопрос:** Может ли `SizeFittingConnectors` изменить `Connector.Id` (и значит `ConnectorIndex`)? Если типоразмер фитинга меняется и Revit пересоздаёт коннекторы — ID могут измениться!

---

## Сценарий 3: Соединение через reducer (футорку)

**Путь:** Init() → InsertReducerSilent → GuessCtcForReducer → AlignFittingToStatic → SizeFittingConnectors → Connect()

**Трассировка:** аналогично Сценарию 2, но:
- `GuessCtcForReducer` использует `CtcGuesser.GuessReducerCtc` — гранулярный fallback
- `SizeFittingConnectors` с `adjustDynamicToFit: false`

**Проверить всё то же что в сценарии 2, плюс:**
- [ ] `GuessReducerCtc` при `staticCTC == dynamicCTC` (одинаковые типы, reducer только по размеру): `(bothSame, bothSame)` — оба коннектора reducer получают одинаковый CTC — корректно
- [ ] `GuessReducerCtc` при cross-type + no direct rules: fallback → реверс `(dynamicCTC, staticCTC)` — conn к static получает dynamicCTC, conn к dynamic получает staticCTC — **перекрёстное назначение**. Проверить что AlignFittingToStatic стратегия 0 (cross-connect) согласуется с этим: `fitConn1 = dynMatch.Conn` (CTC==dynamicCTC → к static). **Но в GuessReducerCtc conn к static = dynamicCTC — значит AlignFittingToStatic найдёт match для dynamicCTC и поставит его к static — корректно**
- [ ] `GuessReducerCtc` при partial match (только static counterpart найден): `dynamicCTC` используется для ForDynamicSide — но `dynamicCTC` это CTC dynamic-элемента, не counterpart. Reducer conn к dynamic стороне = dynamicCTC — но reducer должен иметь counterpart для соединения. **Проверить что это логически корректно**

---

## Сценарий 4: Ручное назначение CTC через кнопку ⚙

### 4A: ReassignFittingCtc (строка 2235)

**Путь:** ReassignFittingCtc → BuildCtcItemsFromVirtualStore → диалог → virtualCtcStore.Set → AlignFittingToStatic

**Проверить:**
- [ ] `BuildCtcItemsFromVirtualStore(fittingId, types)` (строка 1988):
  - `conns = _connSvc.GetAllConnectors(_doc, elementId)` — `c.ConnectorIndex = (int)c.Id`
  - `vCtc = _virtualCtcStore.Get(elementId, c.ConnectorIndex)` — ключ совпадает
  - `item.ConnectorIndex = c.ConnectorIndex` — переносится корректно
- [ ] После диалога (строка 2252): `item.SelectedType is not null` → `virtualCtcStore.Set(_currentFittingId, item.ConnectorIndex, ctc, item.SelectedType)` — **с typeDef!** → pending write создаётся
- [ ] Переориентация (строка 2267): `AlignFittingToStatic` с новыми ctcOverrides → **фитинг может физически перевернуться** если CTC-стороны поменялись
- [ ] После переориентации → `SizeFittingConnectors` — проверить что размеры не слетели

### 4B: ReassignReducerCtc (строка 2300)

**Путь:** аналогично 4A для reducer

**Проверить:**
- [ ] Те же проверки что в 4A
- [ ] `_activeFittingRule` может быть null если reducer был вставлен автоматически (строка 2578) → `ResolveDynamicTypeFromRule(null)` → `return default` → `dynamicTypeCode = Undefined` → стратегии AlignFittingToStatic 0-3 не сработают → **стратегия 4 (distance)** — проверить что это корректно
- [ ] После ReassignReducerCtc: `SizeFittingConnectors(_doc, _primaryReducerId, reorientedReducerConn2, adjustDynamicToFit: false)` → дополнительная корректировка позиции dynamic

---

## Сценарий 5: FlushVirtualCtcToFamilies — запись в семейство (КРИТИЧЕСКИ)

**Путь:** Connect() → PromoteGuessedCtcToPendingWrites → FlushVirtualCtcToFamilies

**Трассировка FlushVirtualCtcToFamilies** (строка 3648):
1. `GetPendingWrites()` → `(ElementId, ConnectorIndex, TypeDef)` — ConnectorIndex = `(int)connector.Id`
2. Группировка по ElementId
3. FamilyInstance → `ApplyFittingCtcToFamily(symbol, items, projectElementId: elemId)`

**Трассировка ApplyFittingCtcToFamily** (строка 3392):
1. `EditFamily(symbol.Family)` → familyDoc
2. `connElems = FilteredElementCollector(familyDoc).OfCategory(OST_ConnectorElem)` — порядок **НЕ гарантирован**
3. `BuildSpatialCtcMap(projectElementId, items, connElems)`:
   - `projectConns = _connSvc.GetAllConnectors(_doc, projectElementId)` — коннекторы project-level элемента
   - `transform.OfPoint(ce.Origin)` — переводим connElem origin из family space → global
   - Для каждого connElem находим ближайший project connector
   - `itemByConnIdx.TryGetValue(nearest.ConnectorIndex, out var item)` — ищем item по `ConnectorIndex` ближайшего project коннектора
   - `result[i] = item` — ключ = индекс connElem в списке
   - **Если `result.Count != items.Count` → возвращает null → fallback!**

4. Если spatialMap ok: `spatialMap[i]` → `connElems[i]` — записываем CTC
5. Если spatialMap null → order matching:
   - `sortedConnElems = connElems.OrderBy(ce => ce.Id.Value)` — сортировка по ElementId
   - `sortedProjectConns = projectConns.OrderBy(pc => pc.ConnectorIndex)` — сортировка по ConnectorIndex
   - Маппинг: sortedConnElems[i] ↔ sortedProjectConns[i] → берём item по `sortedProjectConns[i].ConnectorIndex`
6. Если orderMap null → positional fallback: `orderedItems[i]` → `connElems[i]`

**Проверить (КРИТИЧЕСКИ ВАЖНО):**
- [ ] `BuildSpatialCtcMap`: если 2 connElems находятся на одинаковом расстоянии от project коннекторов → `nearest` будет нестабильным → `usedItems` предотвращает дублирование, но `result` может быть неполным → fallback
- [ ] `BuildSpatialCtcMap`: если фитинг был перемещён (AlignFittingToStatic), позиции projectConns отражают текущее положение фитинга в проекте. connElems — в family space. `transform.OfPoint(ce.Origin)` использует `instance.GetTotalTransform()` — это transform текущего положения фитинга. **После Move+Rotate transform актуален.** Но если SizeFittingConnectors изменил типоразмер — коннекторы могли сдвинуться внутри family space — проверить
- [ ] Order matching: `sortedProjectConns.OrderBy(pc => pc.ConnectorIndex)` — ConnectorIndex = `(int)connector.Id`. Для типичного 2-коннекторного фитинга: Id=1, Id=2 → порядок [1,2]. `sortedConnElems.OrderBy(ce => ce.Id.Value)` — ElementId в family doc. **Нет гарантии что ElementId коннекторов 1,2 совпадают с Connector.Id 1,2!** — проверить
- [ ] Positional fallback: `orderedItems[i]` → `connElems[i]` — `orderedItems` = входной `items` (не отсортирован!). Порядок items = порядок `group.Select(...)` из pending writes. Порядок pending writes = порядок `_pendingWrites` dictionary — **не гарантирован!**
- [ ] `familyDoc.LoadFamily(_doc, new FamilyLoadOptions())` — `overwriteParameterValues = true` — **может сбросить параметры других типоразмеров!**
- [ ] `familyDoc?.Close(false)` — в finally. Если `familyTx` не был закоммичен (anyWritten=false) → Close без коммита → OK. Но если исключение после Commit но до LoadFamily → familyDoc закроется без LoadFamily → **CTC записан в family doc, но НЕ загружен обратно в проект!**

---

## Сценарий 6: Прямой коннект (S1.1/S2.1) + Flush для static/dynamic

**Путь:** PipeConnectCommand.EnsureTypeCode → virtualCtcStore.Set(elemId, connIdx, ctc, selected) → Connect() → FlushVirtualCtcToFamilies

**Проверить:**
- [ ] `EnsureTypeCode` для FamilyInstance (строка 241): `virtualCtcStore.Set(proxy.OwnerElementId, proxy.ConnectorIndex, ctc, selected)` — pending write с 1 коннектором
- [ ] Flush: `items.Count = 1`, но `connElems.Count` может быть 2+. Spatial match: matched 1, items 1 → `result.Count(1) == items.Count(1)` → **ОК, spatial map валиден!**
- [ ] Но spatial match нашёл connElem ↔ project connector по distance. Если у крана 2 коннектора и расстояния до одного из них совпадают → может быть неточный match
- [ ] **Только нужный connElem получит CTC, а второй останется нетронутым** — проверить что цикл `for (int i = 0; i < connElems.Count; i++)` с `if (!spatialMap.TryGetValue(i, out var item)) continue` — корректно пропускает остальные

---

## Сценарий 7: Смена фитинга (Delete+Insert = новый ElementId)

**Путь:** InsertFittingSilent → DeleteElement(oldFittingId) → InsertFitting(newId) → GuessCtcForFitting(newId)

**Проверить:**
- [ ] `DeleteElement` удаляет старый фитинг → ElementId освобождается
- [ ] `InsertFitting` создаёт новый элемент с **новым ElementId**
- [ ] Старые overrides в VirtualCtcStore (для старого ElementId) — **НЕ очищаются!** — `VirtualCtcStore` не имеет метода удаления по ElementId
- [ ] `GuessCtcForFitting(newId)` записывает overrides для **нового** ElementId — старые записи становятся «мёртвыми» (не мешают, но засоряют память)
- [ ] `PromoteGuessedCtcToPendingWrites` промоутит только для `_currentFittingId` (новый Id) — старые pending writes для удалённого элемента **остаются в store!** — `GetPendingWrites()` вернёт их → Flush попытается записать CTC для **удалённого элемента** → `_doc.GetElement(elemId)` вернёт null → `continue` — OK, но проверить

---

## Сценарий 8: Reducer через NetworkMover.InsertReducer (в Connect)

**Путь:** Connect() → _networkMover.InsertReducer(doc, staticConn, dynR, directConnectRules) → GuessCtcForReducer

**Проверить:**
- [ ] `_networkMover.InsertReducer` не принимает `ctcOverrides` (сигнатура INetworkMover: `InsertReducer(doc, parentConn, childConn, ctcOverrides?, directConnectRules?)`). **Но в вызове (строка 2588-2590):** `_primaryReducerId = _networkMover.InsertReducer(doc, _ctx.StaticConnector, dynR, directConnectRules: _mappingRepo.GetMappingRules())` — **ctcOverrides НЕ передан!** — внутри InsertReducer будет использоваться `null` для ctcOverrides → AlignFittingToStatic будет читать CTC из Description (реального) → **если Description ещё пуст → CTC = Undefined → distance fallback**
- [ ] После InsertReducer: `GuessCtcForReducer(_primaryReducerId)` — записывает guessed CTC в store. Но AlignFittingToStatic УЖЕ отработал внутри InsertReducer с ctcOverrides=null → **фитинг мог быть ориентирован неправильно!**
- [ ] `SizeFittingConnectors(_doc, _primaryReducerId, null, adjustDynamicToFit: false)` — fitConn2 = null → метод может не знать какой коннектор к dynamic стороне

---

## Сценарий 9: Connect() → ValidateAndFixBeforeConnect + ResolveConnectorSidesForElement

**Путь:** Connect() → ValidateAndFixBeforeConnect (строка 2705) → ResolveConnectorSidesForElement (строка 2077)

**Проверить:**
- [ ] ValidateAndFix: `fConns = _connSvc.GetAllFreeConnectors(doc, _currentFittingId)` — только свободные коннекторы. Если фитинг уже частично подключён (edge case) → список может быть неполным
- [ ] ResolveConnectorSidesForElement:
  - `connCtcMap = conns.Select(c => (Conn: c, Ctc: GetEffectiveConnectorCtc(elementId, c))).ToList()`
  - `GetEffectiveConnectorCtc` → store.Get(elementId, conn.ConnectorIndex) ?? conn.ConnectionTypeCode
  - Если после SizeFittingConnectors коннекторы были refresh'ed с новыми ConnectorIndex (другой Id) → store.Get вернёт null → fallback на Description → Description ещё не записан → **CTC = Undefined**
- [ ] CanDirectConnect с Undefined CTC → возвращает false → skip
- [ ] Distance fallback в ResolveConnectorSidesForElement: `OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, _ctx.StaticConnector.OriginVec3))` — **после выравнивания fitConn1 ИДЕАЛЬНО совпадает со static** → расстояние = 0 → корректно

---

## Сценарий 10: Цепочка соединений (Chain mode)

**Путь:** ConnectAllChain → для каждого элемента в цепочке → Connect

**Проверить:**
- [ ] VirtualCtcStore — **разделяется между всеми соединениями в цепочке** (один экземпляр на PipeConnectCommand). Если первый Connect выполнил Flush → pending writes очищены? **НЕТ!** — `FlushVirtualCtcToFamilies` не вызывает `Clear()`. Pending writes остаются → **следующий Flush попытается записать их повторно** для того же элемента → `ApplyFittingCtcToFamily` с уже записанными CTC → `anyWritten` может быть false если CTC совпадает → OK, но лишняя работа
- [ ] Если цепочка содержит элементы с **разными** CTC → store содержит overrides для всех → Flush группирует по ElementId → корректно

---

## Сценарий 11: PromoteGuessedCtcToPendingWrites — потеря данных

**Трассировка PromoteElementCtcToPendingWrites** (строка 2034):
1. `overrides = _virtualCtcStore.GetOverridesForElement(elementId)` — все overrides
2. `conns = _connSvc.GetAllConnectors(_doc, elementId)` — текущие коннекторы
3. `allDefined = conns.Count >= 2 && conns.All(c => c.ConnectionTypeCode.IsDefined)`
4. Если allDefined: сравниваем каждый виртуальный CTC с реальным → если allMatch → **return (skip)**
5. Если NOT allMatch: **переход к промоуту**
6. `foreach (connIdx, ctc) in overrides`: `FindTypeDef(ctc)` → если найден → `store.Set(elemId, connIdx, ctc, typeDef)`

**Проверить:**
- [ ] `allDefined` проверяет `conns.Count >= 2` — но что если фитинг имеет 3+ коннекторов (тройник, крестовина)? Все 3 должны быть определены — но в store может быть только 2 override → 3-й undefined → `allDefined = false` → промоут создаст pending write для 2 из 3 — корректно
- [ ] `FindTypeDef(ctc)` — если CTC был guessed как `Undefined` (fallback в CtcGuesser) → `FindTypeDef` вернёт null → **pending write НЕ создаётся** → CTC НЕ записывается → при следующем соединении фитинг всё ещё без CTC → **повторный EnsureTypeCode или guess**
- [ ] `overrides` содержит ключи из store. Если между Guess и Promote произошло перевставление (новый ElementId) → overrides для старого Id → `GetOverridesForElement` вернёт пусто → промоут skip → **CTC теряется!** — но это OK если newId получил свои overrides через TransferOverrides или новый Guess

---

## Сценарий 12: AlignFittingToStatic — конфликт стратегий

**Трассировка AlignFittingToStatic** (RevitFittingInsertService:36):

Стратегии выполняются последовательно: 0(direct) → 0(cross) → 1 → 2 → 3 → 4

**Проверить:**
- [ ] **Стратегия 0 (direct-connect):** проверяет `CanDirectConnect(left.Ctc, staticCTC, rules)` и `CanDirectConnect(right.Ctc, dynamicCTC, rules)`. Если `left.Ctc == Undefined` → `CanDirectConnect` вернёт false → корректно
- [ ] **Стратегия 0 (cross-connect):** `staticCTC != dynamicCTC` + match по значению CTC. Если ctcOverrides содержат guessed CTC, а Description пуст → lookup из ctcOverrides → OK. Но если **обратный** порядок: guessed CTC для static-side = dynamicCTC (перекрёстное) → match найдёт `staticMatch = conn с CTC==staticCTC` — но в guessed CTC для static-side может быть dynamicCTC! — **проверить что перекрёстное назначение согласуется**
- [ ] **Стратегия 1:** `conn с CTC == staticCTC → fc1`. Если guessed CTC для static-side ≠ staticCTC (например, counterpart) → эта стратегия не сработает → fallback к 2/3/4
- [ ] **Стратегия 4 (distance):** если все CTC = Undefined → фитинг ориентируется по расстоянию. **Но фитинг только что вставлен в staticOrigin → оба коннектора на одинаковой позиции!** → `OrderBy(DistanceTo)` нестабилен → fitConn1/fitConn2 могут быть любыми

---

## Граничные условия (edge cases)

Для **каждого** сценария проверь также:

### EC-1: 3+ коннектора (тройник, крестовина)
- GuessCtcForFitting/GuessReducerCtc: `conns.Count >= 2` — OK, но `connForDynamic = conns.First(c => c.ConnectorIndex != connForStatic.ConnectorIndex)` — берёт **первого** с другим индексом. Для тройника: 3 коннектора, но угадываются только 2 — **3-й остаётся без CTC**
- BuildSpatialCtcMap: `result.Count != items.Count` при items=2 и connElems=3 → fallback → orderMap: `sortedConnElems.Count=3 > sortedProjectConns.Count=3` но items только для 2 коннекторов → `itemByConnIdx` содержит 2 ключа → orderMap может содержать 2 записи из 3 → orderMap.Count=2 ≠ 3 → но сравнивается с items.Count=2 → OK

### EC-2: Оба коннектора на одинаковом расстоянии от static
- GuessCtcForFitting: `OrderBy(DistanceTo(staticOrigin)).First()` — недетерминировано → `connForStatic` может быть любым → CTC записываются на коннекторы **случайно**
- AlignFittingToStatic стратегия 4: та же проблема
- BuildSpatialCtcMap: `nearest` нестабилен при одинаковых расстояниях → результат зависит от порядка перебора

### EC-3: CTC = Undefined после guess
- GuessAdapterCtc с пустыми rules → `(staticCTC, dynamicCTC)` как fallback → если staticCTC=Undefined → ForStatic=Undefined
- Store.Set с Undefined → при Get вернёт Undefined → AlignFittingToStatic стратегии не сработают → distance fallback
- FindTypeDef(Undefined) → null → pending write НЕ создаётся → CTC НЕ записывается в семейство → **бесконечный цикл при повторном соединении**

### EC-4: FlushVirtualCtcToFamilies при IsModifiable=true
- `ApplyFittingCtcToFamily` проверяет `_doc.IsModifiable` (строка 3396) → если true → **пропускает запись** → CTC НЕ записывается. Это может произойти если Flush вызывается внутри открытой транзакции — проверить что в Connect() Flush вызывается **МЕЖДУ** транзакциями (до "ConnectTo" транзакции)

### EC-5: LoadFamily побочные эффекты
- `familyDoc.LoadFamily(_doc, new FamilyLoadOptions())` с `overwriteParameterValues = true` — может сбросить параметры типоразмеров. Если 2 фитинга одного семейства с разными CTC → второй Flush перезапишет первый
- **Критический сценарий:** фитинг A (Семейство X, типоразмер Y) → Flush записывает CTC=5 на conn[0]. Фитинг B (Семейство X, типоразмер Y) → Flush записывает CTC=3 на conn[0]. **Оба используют один FamilySymbol!** LoadFamily для B перезапишет CTC для A?

### EC-6: VirtualCtcStore не очищается между сессиями
- `VirtualCtcStore` создаётся в PipeConnectCommand (строка 75) и передаётся в ViewModel через ctx → одноразовый → OK
- Но если пользователь закрывает окно и открывает заново — новый экземпляр → OK

### EC-7: Flush для MEPCurve (труба/FlexPipe)
- `elem is MEPCurve or FlexPipe` → `_familyConnSvc.SetConnectorTypeCode(doc, elemId, connIdx, typeDef)` — **внутри RunInTransaction**. Но Flush вызывается **МЕЖДУ** транзакциями Connect() — проверить что `_groupSession?.RunInTransaction` корректно работает в этом контексте

### EC-8: Connector.Id после изменения типоразмера
- Если SizeFittingConnectors меняет типоразмер FamilyInstance через параметр → Revit может пересоздать коннекторы → **новые Connector.Id** → store.Get по старому ключу вернёт null
- Проверить: RefreshConnector после Size → если ConnectorIndex изменился → нужно TransferOverrides или re-guess

---

## Методика проверки

Для каждого метода с CTC:
1. Прочитай код метода **полностью** (все строки тела)
2. Определи тип ключа: это `connector.Id` или порядковый индекс?
3. Проследи откуда берётся ключ и куда он передаётся — **пошагово**
4. Проверь что при refresh/recreate коннектора ключ не меняется
5. Для каждого метода спроси себя: «Что если этот метод получит Undefined CTC?»
6. Для каждого метода спроси себя: «Что если коннекторов 3+, а не 2?»
7. Для каждого метода спроси себя: «Что если spatial matching не сработает (равные расстояния)?»

## Ожидаемый результат

1. **Список найденных багов** с файлом:строка и описанием (по приоритету: критические → средние → мелкие)
2. **Исправление** каждого найденного бага
3. `dotnet build` — без ошибок
4. `dotnet test` — все тесты проходят
5. **Краткое резюме** что было исправлено и что потребует тестирования в Revit

---

## Методы для обязательного прочтения (чеклист)

Прочитай **каждый** из этих методов полностью перед началом аудита:

- [ ] `PipeConnectEditorViewModel.GuessCtcForFitting` (строка 1901)
- [ ] `PipeConnectEditorViewModel.GuessCtcForReducer` (строка 1945)
- [ ] `PipeConnectEditorViewModel.PromoteGuessedCtcToPendingWrites` (строка 2028)
- [ ] `PipeConnectEditorViewModel.PromoteElementCtcToPendingWrites` (строка 2034)
- [ ] `PipeConnectEditorViewModel.GetEffectiveConnectorCtc` (строка 2071)
- [ ] `PipeConnectEditorViewModel.ResolveConnectorSidesForElement` (строка 2077)
- [ ] `PipeConnectEditorViewModel.BuildCtcItemsFromVirtualStore` (строка 1988)
- [ ] `PipeConnectEditorViewModel.FlushVirtualCtcToFamilies` (строка 3648)
- [ ] `PipeConnectEditorViewModel.BuildSpatialCtcMap` (строка 3547)
- [ ] `PipeConnectEditorViewModel.ApplyFittingCtcToFamily` (строка 3392)
- [ ] `PipeConnectEditorViewModel.RefreshWithCtcOverride` (строка 1889)
- [ ] `PipeConnectEditorViewModel.InsertReducerSilent` (строка 2115)
- [ ] `PipeConnectEditorViewModel.InsertFittingSilent` (строка 2402)
- [ ] `PipeConnectEditorViewModel.ReassignFittingCtc` (строка 2235)
- [ ] `PipeConnectEditorViewModel.ReassignReducerCtc` (строка 2300)
- [ ] `PipeConnectEditorViewModel.Connect` (строка 2565)
- [ ] `PipeConnectEditorViewModel.ValidateAndFixBeforeConnect` (строка 2705)
- [ ] `PipeConnectEditorViewModel.ResolveDynamicTypeFromRule` (строка 1876)
- [ ] `RevitFittingInsertService.AlignFittingToStatic` (строка 36)
- [ ] `ConnectorWrapper.ToProxy` (строка 17)
- [ ] `VirtualCtcStore.Set` (строка 20)
- [ ] `VirtualCtcStore.GetOverridesForElement` (строка 38)
- [ ] `VirtualCtcStore.GetPendingWrites` (строка 53)
- [ ] `VirtualCtcStore.TransferOverrides` (строка 75)
- [ ] `CtcGuesser.GuessAdapterCtc` (строка 50)
- [ ] `CtcGuesser.GuessReducerCtc` (строка 73)
- [ ] `CtcGuesser.CanDirectConnect` (строка 12)
- [ ] `CtcGuesser.FindDirectConnectCounterpart` (строка 32)
- [ ] `PipeConnectCommand.EnsureTypeCode` (строка 207)
- [ ] `PipeConnectCommand.IsKnownTypeCode` (строка 196)
