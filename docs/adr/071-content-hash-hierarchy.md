# ADR-071: Иерархия контент-хэша FamilyManager — секции, per-type, CAS-пул превью (#249)

## Статус

Accepted (2026-08-28). Реализовано в ветке `feature/249-hash-hierarchy` (24 коммита).
Формат хэша: **FHV16** (путь FHV12→16 за одну ветку; в релиз уходит только FHV16).
Floor совместимости: `2.0.1-beta.10` (ни одна промежуточная версия не шипилась).

## Контекст

Issue #249 ставил задачи: точная дедупликация (для будущего облака), per-type stale
вместо семейного, ответ «что именно изменилось» пользователю, экономия хранения
превью. Пять раундов ручного стресс-теста вскрыли серию системных проблем
детерминизма хэша — каждая закрыта отдельной версией формата.

## Решение

### 1. Секционная декомпозиция хэша

Каноническая строка разбита на **13 loadable-секций** (`ContentSectionHash`):
META, PARAMS, TYPES, PHANTOM, DEF, GEOM, GEOM2D, NESTED, NONSHARED, NESTEDHASH,
FACTS, FLAGS, CONN (+LOOKUP при наличии lookup-таблиц). Секции хранятся в
`catalog_versions.section_hashes`/`section_strings` (V33, JSON) — это включило
**криминалистику по живой базе** (дифф любых двух версий токен-в-токен без Revit).

**Автономность секций (FHV14, критично понимать):**

| Секция | Содержит | Стреляет когда |
|---|---|---|
| GEOM | 3D-формы: метрики солидов, материал (RGBA), размещения вложенных (NESTEDINST) | тело добавлено/изменено/сдвинуто |
| GEOM2D | **только свободная 2D-графика**: symbolic/detail/model линии, текст | свободная линия/текст |
| DEF | **только привязки**: формы с биндингами параметров, размеры **с метками**, опорные плоскости | перепривязка, смена метки |
| TYPES/VALUES | per-type значения | правка значения типа |

Классификатор изменений (`ContentChangeClassifier`): major = DEF/GEOM/CONN/
STRUCT/ROUTING; minor = TYPES/VALUES/PHANTOM/NESTED*/LOOKUP/FLAGS/PARAMS;
trivial = остальное. Diff-окно (бейдж ⇄ у статуса Existing) показывает класс +
локализованные имена секций + per-type списки; для trivial — действие
«Перезаписать текущую версию».

### 2. Per-type хэши и per-type stale

`family_type_hashes` (V32): SHA-256 канонической подстроки типа из TYPES
(только значения — **не зависят от FHV12–16**, не пересчитывались).
Ключ — `SystemTypeIdentityKey` (loadable: UPPER(имя типа)).

**Семантика stale-индикаторов:**

| Что изменилось | Индикаторы |
|---|---|
| Значение одного типа | точка только на нём |
| Любая общая секция (GEOM/DEF/CONN/…) | точки на всех загруженных типах |

Вердикт **VersionMismatch** (маркер v_old vs current): карта считается
**чисто из БД** (`ComputeDbPerTypeStaleAsync`) — per-type хэши обеих версий +
секционные хэши (изменилась не-TYPES/VALUES секция = все типы stale).
Ноль открытий документов, иммунитет к open-editor guard (семейство остаётся
открытым в редакторе после «Импорт активного файла» — EditFamily-proof там
недоступен by design). **ContentDrift** (маркер == current, локальная правка):
карта из embedded-верификации (EditFamily vs файл). Confirm-диалог перед
«Обновить» — только для ContentDrift (единственный случай потери данных).

### 3. Детерминизм извлечения (FHV15–16)

- **FHV15 — reference-тип.** Evaluated-секции (GEOM, DEF-offsets, CONN) меряются
  при **первом по алфавиту именованном типе**, а не текущем. Переключение —
  в транзакции с **откатом** (I-03b, паттерн Geo3DPerType): документ не
  пачкается, Regenerate не нужен. Verifier-выравнивание #240
  (`AlignCurrentTypeForVerification`) удалено — restriction-set передаётся в
  экстрактор как предпочтение reference-типа (`preferredTypeNames`).
- **FHV16 — канонизация `-0`.** `FormatCoord` отдаёт `0` для любого значения,
  форматирующегося как `-0` (IEEE -0.0 и малые отрицательные, округляемые в
  ноль). Общий хелпер — GEOM/CONN/NESTEDINST и VIEW3D квантуются одинаково.

### 4. Изоляция sketch-контента (FHV13/14)

- GEOM2D исключает кривые, **зависимые от формы** — `Element.GetDependentElements`
  (Revit 2018+; `Sketch.OwnerId`/`GetAllElements` — 2024+, упали на net48).
  Probe: свободные линии Revit заворачивает в собственные Sketch
  (OwnerId=-1) — фильтрация «по эскизам» съедала их; по зависимостям формы — нет.
- DEF/DIMS и счётчик размеров GEOM2D — **только labeled** (`dim.FamilyLabel`):
  автоматические эскизные размеры Revit (создаются даже для API-вытягиваний,
  НЕ принадлежат коллекции эскиза) не являются привязкой.
- DEF/FORMS — только формы хотя бы с одной привязкой.

### 5. CAS-пул превью (Phase 5)

`files/_shared/models/{shard2}/{view3dHash[..40]}.glb` — иммутабельные файлы
(I-16), связь только через `family_assets.relative_path`. VIEW3D-хэш = SHA-256
нормализованных per-type входов (GLB-filtered формы + nested-размещения;
имя типа НЕ входит — rename-independent; ElementId не входят — GLB content-pure).
Шард = 2 hex (максимум 256 папок); имя файла — 40 hex (160 бит) ради MAX_PATH.

**Двухуровневый reuse:** (1) DEF/GEOM/TYPES секции совпали → re-link ассетов без
рендера (текстовая правка версии = ноль Revit-работы); (2) per-type pool hit —
совпавшие типы переиспользуют GLB, изменённые рендерятся. Refcount-удаление
(файл удаляется при нуле ссылок после DB-commit) + GC-sweep в purge.

## Отклонённые альтернативы

- **Per-family пул превью** (вместо общего) — отклонено владельцем: кросс-семейный
  дедуп (библиотеки с param-only копиями семейств) ценнее ручной находимости файлов.
- **Миграция legacy GLB в пул** — не нужна: старые файлы отдаются как раньше,
  пул наполняется органически; ключ пула (content hash) из старого файла не выводится.
- **form.Visible-фильтр в GEOM** — probe: form.Visible трекает IS_VISIBLE_PARAM,
  фильтр выбросил бы ровно условные формы; вместо этого
  `IncludeNonVisibleObjects=true` + флаги видимости в метриках.

## Последствия

- Миграции: critical `hash-v16` (один раунд — секции пишутся инлайн в той же
  транзакции, `ActualizationSectionComposer`), backstop `section-hashes-v1`,
  `type-hashes-v1` (optional, Order 70/80).
- Известные ограничения (следующая волна): #250 (level-2 nested content в
  VIEW3D), #251 (per-face материалы), #179 (system per-type stale — фундамент
  хранения заложен).
- Тестирование: 15+ новых интеграционных контрактов (reference-тип, sketch-
  исключение, per-type карты, CAS reuse); runbook итераций в smartcon-testing.

## Связи

- ADR-056 (FHV3), ADR-065 (FHV4-5), ADR-066 (FHV8 shared nested), ADR-069 (FHV11 lookup)
- ADR-058 (forward-compat floor), ADR-054 (actualization engine)
- Issue #240 (predecessor: verifier-side current-type alignment — superseded)
