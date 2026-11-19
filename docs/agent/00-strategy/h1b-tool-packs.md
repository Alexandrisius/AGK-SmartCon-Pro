# H1b–H1d: поиск и инструменты под реальные кейсы

> **Статус:** active · 2026-11-18 · владелец: продукт
> **Основание:** ADR-076 (tool surface), ADR-077 (model entity search), реальные кейсы владельца.
> **Место:** сразу после H1, до H2–H5. Vector (H6) — последним.

## 1. Зачем

H1 закрывает фундамент (tool catalog v2, контекст, компакция), но не закрывает
ежедневные BIM-кейсы. После H1 нужен один короткий этап, который даёт агенту
**поиск по модели + инструменты для видов, шаблонов, фильтров, overrides и
семейств**.

Целевые кейсы:

1. Найти арматуру/оборудование по имени семейства/типа, в т.ч. «кран шаровый
   волк», «шаровые краны на 2 этаже», «вентиляторы на 1 этаже».
2. Настроить вид или шаблон вида: скрыть категории/оси, создать фильтр,
   убрать трубы системы Т1, покрасить стены, crop/scope box.
3. Отредактировать семейство: найти семейства с параметром, добавить параметры
   заказчика, перенести значения, разобраться с instance/type, формулами и
   lookup-таблицами.

## 2. H1b — Живой поиск по модели (ADR-077)

**Никакого постоянного индекса.** Модель живая и многопользовательская:
100k экземпляров с разными значениями, изменения каждые минуты, 5 владельцев,
worksharing. Любая индексная БД устаревает и не разделяется между сессиями.

Вместо индекса:

- **нормализация запроса на лету**: токенизация, lowercase, ё→е, русский
  Snowball-стеммер, синонимы (кран/клапан/вентиль, вентилятор/фанкойл,
  арматура/фитинг), алиасы категорий;
- **живые справочники**: `list_categories`, `list_levels`,
  `list_family_types` (включая не размещённые типы), `list_parameters`;
- **`find_families_with_parameter(parameterName)`** — проход по семьям/типам
  (5–20k), без индекса;
- **`query_elements` + `parameterContainsAny`** — категория + уровень + OR по
  вариантам стемминга/синонимов; в результате `matchedParameter`/`matchedValue`;
- **`summarize_parameter_values(category, parameter)`** — частоты и уникальные
  значения для анализа 100k имён, живой проход;
- ранжирование — по числу/точности совпавших токенов в уже найденной выборке,
  без глобального скоринга и без индекса.

Правила:

- запрос всегда сужается категорией (и уровнем/типом, если известны);
- для 100k экземпляров — один scoped-проход; если p95 стабильно >5 с,
  допускается **one-shot in-memory map на время запроса**, не постоянный индекс;
- кэш — только на запрос/ход, сброс по `DocumentChanged`; никакой БД между
  сессиями;
- 200k–1M элементов не индексируются ни в каком виде.

**Приёмка:** семейство `KR_SHAR_VOLK_15` с `ТДСК_Наименование` =
«Запорный вентиль ВОЛК» находится по «запорный вентиль волк» без имени файла;
100k экземпляров одной категории — один scoped-проход; correct entity за ≤2
tool-вызова; false match <5 %; p95 замерить и зафиксировать.

**Приёмка:** семейство `KR_SHAR_VOLK_15` находится по «запорный вентиль волк»
через `ТДСК_Наименование`/описание; «шаровые краны» находят «Кран шаровый»;
«арматура трубопроводов» → `OST_PipeAccessory`; top-5 ≥95 % на 100 реальных
фразах; p95 <100 мс.

## 3. H1c — View configuration pack

Не «скрыть Т1», а generic-класс «настройка вида». Два инструмента:

### 3.1 `configure_view` / `configure_view_template`

`target`: `{kind:"view"|"template", id}`.

Секции:

- `visibility`: категории, worksets, элементы (hide/show);
- `viewRange`, `crop`, `scale`, `detailLevel`, `discipline`;
- `filters`: список правил + действие;
- `overrides`: цвет/штриховка/прозрачность по фильтру.

Если вид управляется шаблоном и настройка контролируется шаблоном — вернуть
`view_controlled_by_template` + `templateId`; не делать молчаливый no-op.

### 3.2 `set_filter_overrides` / `create_view_filter`

Правила — generic DSL:

- `category`, `family`, `type`, `level`, `room`, `workset`, `system`;
- `parameter` + `op` (`eq/neq/contains/gt/...`) + `value`;
- `and/or`.

Примеры, которые НЕ являются отдельными tools:

- «убрать трубы Т1» → фильтр `{category:OST_PipeCurves, parameter:"Система", eq:"Т1"}` + `hide`;
- «покрасить стены» → фильтр `{category:OST_Walls}` + override color;
- «скрыть оси» → `visibility.categories` для `OST_Grids`.

### 3.3 Scope boxes / crop

- `list_scope_boxes`, `assign_scope_box(viewIds, scopeBoxId)`, `clear_scope_box`;
- `copy_crop_from_view` — явный сценарий «вырезать кусок как на эталоне»
  (внутри уже есть `copy_view_settings`, но нужен отдельный контракт).

**Приёмка:** шаблон и вид без шаблона; скрытие категории/осей; фильтр Т1;
override стен; crop/scope box; integration-тест на 5 видах.

## 4. H1d — Family editing pack

> **Реализовано 2026-11-19:** 9 тулов в паке `families` (каталог 42), точный
> undo через обратные операции, бэкап в отдельном family-документе, read-back
> после load-back. Матрица доказательств и негативные кейсы:
> `docs/agent/00-strategy/h1d-evidence.md`; каталог ошибок:
> `docs/agent/family-tools-error-catalog.md`; ручной runbook:
> `docs/agent/family-editing-runbook.md`. Файловый поток (`.rfa` →
> `OpenDocumentFile`) остаётся за рамками 9 тулов — backup рядом с исходным
> `.rfa` делается, если путь известен.

Редактирование **family document**, не параметров проекта.

### 4.1 Открытие/сохранение

- загруженное семейство → `Document.EditFamily(family)` — **без UI**, batch-friendly;
- файл `.rfa` → `Application.OpenDocumentFile`, сохранить/закрыть;
- по умолчанию редактор не показывать; видимый режим — opt-in для отладки.

### 4.2 Чтение

`list_family_parameters(familyId)`:

- имя, `isInstance`, `isShared`, `storageType`;
- `formula`, `isDeterminedByFormula`, lookup-таблица;
- значения по каждому `FamilyType`, не одно «текущее»;
- `canSetValue`, `canSetFormula`.

`get_family_parameter_values` — значения по типам.

### 4.3 Запись

- `rename_family_parameter`;
- `add_family_parameter` (обычный/shared, instance/type);
- `set_family_parameter_values` по типам;
- `set_family_formula`;
- `map_family_parameters(source→target)`:
  - instance→type конфликты обнаруживаются и возвращаются;
  - formula/lookup: копировать формулу или материализовать значения;
  - target с формулой: сначала снять формулу;
- `save_family` / `load_family_back` с `IFamilyLoadOptions`.

### 4.4 Безопасность

- backup `.rfa` перед изменением;
- dry-run diff по всем типам «было → станет»;
- подтверждение на shared parameters/формулы;
- rollback при ошибке; audit;
- понимание, что project/shared parameter в семействе может отсутствовать.

**Приёмка:** семейство с instance/type параметрами, формулой, lookup-таблицей;
кейс «найти семьи с ТДСК_Наименование → добавить параметры заказчика → перенести
значения → сохранить» без открытия редактора.

## 5. Циклические/bulk-операции — H8, не сейчас

В H1/H1b–H1d **нет** cyclic job engine. Есть только:

- `SupportsBulk` в карточке tool;
- write-tools с массивом `items` (цикл внутри C#, одна транзакция, dry-run);
- `execute_plan` для последовательности шагов.

Этого достаточно для «обработай явный список ≤200». Для «найди все семьи с
параметром X и обработай каждое» нужен отдельный эпик **H8 — Bulk/cyclic job
engine**, который проектируется после H1d:

- job spec: `select` (query/filter) + `action` + `batchSize` + `dryRun` +
  `onError`;
- executor: выборка, батчи, прогресс, пауза, отмена, resume, retry;
- транзакции по батчам; audit по элементу;
- альтернатива для сложной логики — sandbox-скрипт (Jint/Lua) поверх
  типизированного фасада (H7), а не runtime C#;
- агент не делает цикл сам — он создаёт job и получает `jobId`.

**Не дублировать:** не добавлять «циклический tool» в H1c/H1d. Сначала
закрываем популярные кейсы массивами и одиночными family edit, затем H8.

## 6. Порядок и метрики

Порядок: **H1 → H1b → H1c → H1d → H8 → H2–H5 → H7 → H6 (vector последний)**.

Метрики этапа:

- case success на 15–20 реальных задачах: ≥80 % после H1c, ≥85 % после H1d;
- 0 per-case tools: каждый инструмент решает ≥3 кейса;
- top-5 поиска ≥95 %; p95 <100 мс;
- dry-run/diff/undo для каждой write-операции;
- integration-тесты на видах, шаблонах, фильтрах, семействах.

## 7. Non-goals

- глобальный индекс 200k–1M элементов;
- vector search по модели (только H6 для каталога тулов/навыков);
- runtime C# / `send_code_to_revit`;
- per-property/per-family tools;
- создание 3D-объектов;
- cyclic job engine до H8.