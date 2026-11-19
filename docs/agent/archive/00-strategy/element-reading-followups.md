> **ARCHIVED (2026-11-19).** Исторический документ. Не руководство к действию.
> Актуально: [`ROADMAP.md`](../../ROADMAP.md) + GitHub Issues (метка `agent`). Внутренние ссылки исторические.

# Element reading profile — P1/P2 follow-ups

> **Статус:** active backlog · 2026-11-19 · ветка `develop`
> **Связано:** ADR-076 (tool = intent), ADR-077 (no persistent model index), `autonomy-roadmap.md` §7 (C1/C2), `../HANDOFF.md`
> **Назначение:** очередь P1/P2 после P0 «definition-first чтение элемента». P0 реализован; этот файл — чтобы не потерять остальное.

## P0 — сделано (контекст)

- `get_element_details`: identity (`typeId`, `familyId`, `familyUniqueId`, `familyKind`), дедупликация типов (`types[]` + `elements[].typeId`), essence по `BuiltInParameter`, раздельные `instanceParameters`/`parameters` типа, классы `system`/`shared`/`family` (по умолчанию system+shared, family — opt-in), счётчики, `readOnlyReason`; без «дельты экземпляра от типа».
- `ElementCard`: `typeId`/`familyId`/`familyUniqueId` — go-to-definition из `query_elements`/`get_selected_elements`.
- `get_element_details` в core-наборе (9 always-available тулов).
- Blast radius: `set_parameters(scope="type")` и family-тулы (`affectedTypes`, `affectedInstances`, `sampleInstanceIds`, `countCapped`, `undoReturnsAllInstances`, `familyReload`, `projected`).
- `AgentProjectContextProvider`: O(N)-скан категорий кэшируется по `DocumentId + DocumentVersion (VersionGUID + NumberOfSaves)`, абсолютный TTL 15 с; активный вид/`IsModified`/счётчики — свежие.
- UI: русские display-имена тулов в live-карточках и при восстановлении истории; сырое имя — в `Details`.
- Гейты: R24/R25/R26/R27 — 0/0; non-live Agent unit 534/534; IT `AgentQueryToolsTests` 8/8 (Revit 2025); валидатор `general` — PASS.

## P1 — следующее

1. **`list_types(category, scope=all|loadable|system, onlyPlaced)`** — один тул над `FamilySymbol` и `ElementType` (`WhereElementIsElementType()` + категория); карточка: `typeId`, `familyId`, `familyName`, `isSystem`, `isPlaced`, `instanceCount`. Закрывает «что ещё можно поставить» для труб/стен: сейчас системных типов (`PipeType`/`WallType`/`DuctType`) нет ни в одном туле.
2. **Починить `list_family_types.includePlaced`** — сейчас флаг не фильтрует, а только вычисляет `isPlaced`.
3. **`list_parameters(category)`** — какие параметры есть, класс (`system`/`shared`/`family`), scope, writable; ответ на «что я могу здесь менять».
4. **Essence в карточке `query_elements`** (та же таблица BIP) + `matchedParameter`/`matchedValue` для MEP; `parameterContainsAny` (ADR-077 §5).
5. **`list_families` в core** (или карта семейств в overview) — definition-слой доступен без discovery.
6. **UI: человекочитаемая карточка элемента** вместо pretty-JSON (identity + essence + ключевые параметры), raw JSON — в expander.
7. **Классификация non-shared project-параметров** — решить `family` vs отдельный `project`/`other` (валидатор отметил: сейчас `family` = остаток, нешаренные project-параметры могут ложно скрываться).
8. **Точный `readOnlyReason`** — formula/computed/shared binding/ownership, где API позволяет; `null` при неизвестности.
9. **Локализация display-имён** через `LocalizationService` (сейчас hardcoded RU; карточки тулов тоже RU).
10. **Essence: не считать 0 пустым** для уклона/offset/boolean; добавить conduit-специфичные BIP, если нужно.
11. **Integration coverage:** default-скрытие family/project, `parameterClasses` (system/shared/family/invalid), `readOnlyReason`, essence type fallback, дедуп типов на нескольких элементах, type-element input, `blastRadius`/`familyReload`, no-op type-scope, кэш (TTL/смена документа).
12. **Payload-бюджет** `query_elements`/`get_selected_elements` после `familyUniqueId` (до 200 карточек).

## P2 — после P1

13. **Протокол в system prompt:** карта → определения → экземпляры → чтение типа+экземпляра → запись с явным `family`/`type`/`instance` и blast radius.
14. **Eval качества ответа:** «что это за элемент», «что за модель», выбор scope; проверить routing-сдвиг после тегов `get_element_details` («тип», «семейство»).
15. **Карта глубины 2** (category→family→type) — только по запросу, не авто-инъекция.
16. **`get_project_overview`** — переиспользовать кэш провайдера в пределах хода или оставаться явно on-demand.
17. **Полный IT-сьют + eval replay + Live-тесты**; R24/R26 интеграционные прогоны (R24 — net48, R26 — API 2026).

## Остаточные риски / ограничения

- `src/SmartCon.Revit/Agent/AgentDocumentState.cs` — untracked, но обязателен для кэша и write-pipeline; включать в коммит.
- Agent компилируется только с R24: R19/R21 исключают `Agent\**\*.cs` в `SmartCon.Revit.csproj` (ForgeTypeId-era API).
- Workshared write запрещён (`workshared_not_supported`); ownership в blast radius станет актуален только при появлении поддержки.
- Вложенные/общие семейства, parts, assemblies и linked models не входят в `affectedInstances` — в ответе есть `note`.
- `familyParametersOmitted` считается только по параметрам, прошедшим name-фильтры; при `parameterNames`/`parameterNameContains` это осознанное сужение.