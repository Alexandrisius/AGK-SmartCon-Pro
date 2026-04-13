# Промпт для аудита стабильности CTC-механизма SmartCon

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

## Твоя задача: полный аудит стабильности

Пройди **каждый** из перечисленных ниже сценариев и проверь корректность. Для каждого:
- Прочитай код
- Трассируй значения ключей (ConnectorIndex, ElementId, ctcOverrides keys)
- Проверь что данные не теряются между шагами
- Если найдёшь баг — исправь

### Сценарий 1: Прямое соединение двух кранов (без фитинга)
**Путь:** PipeConnectCommand → S1.1 (EnsureTypeCode) → Init() → Connect() → ConnectTo
**Проверить:**
- VirtualCtcStore.Set в EnsureTypeCode использует `proxy.ConnectorIndex` — это `connector.Id` (int cast), а НЕ порядковый индекс
- После `RefreshWithCtcOverride` в Init() виртуальный CTC не теряется
- В Connect() → ResolveConnectorSidesForElement → GetEffectiveConnectorCtc возвращает правильный CTC

### Сценарий 2: Соединение через фитинг (адаптер)
**Путь:** Init() → InsertFittingSilent → GuessCtcForFitting → AlignFittingToStatic → SizeFittingConnectors → RealignAfterSizing → Connect()
**Проверить:**
- `GuessCtcForFitting` использует spatial matching (connForStatic = ближайший к staticProxy.Origin)
- `_virtualCtcStore.GetOverridesForElement` возвращает ключи по `ConnectorIndex` (это connector.Id)
- В `AlignFittingToStatic` ключи `ctcOverrides.TryGetValue((int)c.Id, ...)` — `c.Id` это Revit Connector.Id. **Это должно совпадать** с ключами из VirtualCtcStore
- После RealignAfterSizing → RefreshConnector коннекторы получают те же ConnectorIndex
- В Connect() → ResolveConnectorSidesForElement → GetEffectiveConnectorCtc читает из store по правильному ключу

### Сценарий 3: Соединение через reducer (футорку)
**Путь:** Init() → InsertReducerSilent → GuessCtcForReducer → AlignFittingToStatic → SizeFittingConnectors → Connect()
**Проверить всё то же что в сценарии 2, плюс:**
- `GuessCtcForReducer` назначает CTC на основе spatial matching
- FlushVirtualCtcToFamilies → BuildSpatialCtcMap → ключи = индексы connElems (порядок FilteredElementCollector)
- Spatial matching: `transform.OfPoint(ce.Origin)` → сравнение с `projectConns[i].Origin`
- Если spatial matching fails (возвращает null) → fallback на позиционный маппинг корректен

### Сценарий 4: Ручное назначение CTC через кнопку ⚙ (ReassignReducerCtc / ReassignFittingCtc)
**Путь:** ReassignReducerCtcCommand → BuildCtcItemsFromVirtualStore → диалог → virtualCtcStore.Set → AlignFittingToStatic
**Проверить:**
- `BuildCtcItemsFromVirtualStore` читает `c.ConnectorIndex` из `_connSvc.GetAllConnectors` — это connector.Id
- После диалога `item.ConnectorIndex` используется как ключ в `_virtualCtcStore.Set`
- `typeDef` передаётся в Set — создаётся pending write
- После переориентации ctcOverrides содержат правильные ключи

### Сценарий 5: FlushVirtualCtcToFamilies (запись в семейство)
**Путь:** Connect() → PromoteGuessedCtcToPendingWrites → FlushVirtualCtcToFamilies
**Проверить (КРИТИЧЕСКИ ВАЖНО):**
- `GetPendingWrites()` возвращает `(ElementId, ConnectorIndex, TypeDef)` — ConnectorIndex здесь = connector.Id
- В `FlushVirtualCtcToFamilies` items создаются с `ConnectorIndex = w.ConnectorIndex`
- `BuildSpatialCtcMap` получает эти items и маппит: для каждого connElem[i] находит ближайший project connector, берёт item по project connector.ConnectorIndex
- Write loop: `spatialMap[i]` → `connElems[i]` — **только** при совпадении индекса
- Если spatial map = null (fallback): позиционный маппинг `items[j]` → `connElems[j]` — проверить что порядок items совпадает с порядком connElems

### Сценарий 6: Прямой коннект (S1.1/S2.1) + FlushVirtualCtcToFamilies
**Путь:** PipeConnectCommand.EnsureTypeCode → virtualCtcStore.Set(elemId, connIdx, ctc, typeDef) → Connect() → FlushVirtualCtcToFamilies
**Проверить:**
- `EnsureTypeCode` для FamilyInstance: `virtualCtcStore.Set(proxy.OwnerElementId, proxy.ConnectorIndex, ctc, selected)` — `proxy.ConnectorIndex = (int)connector.Id`
- В FlushVirtualCtcToFamilies для FamilyInstance: `ApplyFittingCtcToFamily(symbol, items, projectElementId: elemId)` — передаётся elemId для spatial matching
- Spatial match находит connElem ↔ project connector по origin → item по ConnectorIndex
- **Проблема:** pending write имеет только 1 item (один коннектор). `items.Count=1`, но `connElems.Count=2`. Spatial match найдёт 1 из 2 → `result.Count(1) != items.Count(1)` — это OK. Но нужно проверить что **только нужный** connElem получит CTC, а второй останется нетронутым

## Ключевые типы и их значения (шпаргалка)

- `ConnectorProxy.ConnectorIndex` = `(int)connector.Id` — Revit-внутренний ID коннектора (из `ConnectorWrapper.ToProxy`)
- `Connector.Id` (Revit API) — уникальный ID коннектора в рамках элемента. НЕ порядковый индекс! Может быть 1, 2, 3 или любым другим int
- `ctcOverrides` ключи = `ConnectorProxy.ConnectorIndex` = `(int)connector.Id`
- В `AlignFittingToStatic`: `ctcOverrides.TryGetValue((int)c.Id, ...)` — `c` это Revit `Connector`, `c.Id` это тот же ID
- В `BuildSpatialCtcMap`: ключ результата = индекс в `connElems` (порядок FilteredElementCollector)
- `connElems` порядок = `FilteredElementCollector` по `OST_ConnectorElem` — НЕ гарантирован, но стабилен в рамках одного family doc

## Файлы для аудита

| Файл | Что проверять |
|---|---|
| `src/SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` | Все методы с CTC: GuessCtcForFitting, GuessCtcForReducer, PromoteElementCtcToPendingWrites, FlushVirtualCtcToFamilies, BuildSpatialCtcMap, ApplyFittingCtcToFamily, BuildCtcItemsFromVirtualStore, RefreshWithCtcOverride, GetEffectiveConnectorCtc, ResolveConnectorSidesForElement, InsertReducerSilent, Connect |
| `src/SmartCon.Revit/Fittings/RevitFittingInsertService.cs` | AlignFittingToStatic: как ctcOverrides ключи используются, стратегии 0-4 |
| `src/SmartCon.Revit/Wrappers/ConnectorWrapper.cs` | ToProxy: ConnectorIndex = (int)connector.Id — корневой источник ключей |
| `src/SmartCon.Core/Models/VirtualCtcStore.cs` | Все методы: Set/Get/GetOverridesForElement/GetPendingWrites |
| `src/SmartCon.Core/Models/CtcGuesser.cs` | GuessAdapterCtc, GuessReducerCtc, FindDirectConnectCounterpart |
| `src/SmartCon.PipeConnect/Commands/PipeConnectCommand.cs` | EnsureTypeCode, IsKnownTypeCode — как virtualCtcStore.Set вызывается |

## Методика проверки

Для каждого метода с CTC:
1. Прочитай код метода полностью
2. Определи тип ключа: это `connector.Id` или порядковый индекс?
3. Проследи откуда берётся ключ и куда он передаётся
4. Проверь что при refresh/recreate коннектора ключ не меняется
5. Проверь edge cases: 1 коннектор, 3+ коннекторов, все CTC already defined

## Ожидаемый результат

- Список найденных багов с файлом:строка и описанием
- Исправление каждого найденного бага
- Запуск `dotnet build` и `dotnet test` после исправлений
- Краткое резюме что было исправлено
