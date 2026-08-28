# ADR-072: Ручной staging мини-проектов MEPCurve + routing как данные каталога (Issue #254)

**Date:** 2026-08-28
**Status:** proposed (после утверждения плана владельцем → accepted)
**Related:** Issue #254, ADR-061 (system family sync), ADR-062 (mini-project marker), ADR-064 (family key), ADR-065 (FHV4), ADR-066 (dependencies model), ADR-067 (dependency guard), ADR-055 (family facts / part_type), ADR-054 (actualization engine), ADR-071 (content hash hierarchy), #104, #178, #183, #188, #190

> **Назначение документа:** полный самодостаточный контекст расследования #254 и план
> рефакторинга. Новый агент обязан прочитать этот ADR целиком ПЕРЕД любой работой
> по теме — повторное расследование не требуется, все факты ниже доказаны
> экспериментально (probe-прогоны в реальном Revit + диффы section_strings в БД).

## 1. Context: криминалистика #254 (всё доказано, не гипотезы)

### 1.1 Симптом

Переимпорт НЕИЗМЕННОЙ системной трубы (+ её routing-зависимости) показывал фантомные
«Существующее» вместо «Дубликат»: у трубы — 1 токен в ROUTING (материал сегмента
`'KAN-therm - Inox'` → `'KAN-therm - Inox1'`), у фитинга `BP_A0302` — 1 токен в PARAMS
(материал-параметр `BP_CaseMaterial` → то же имя с суффиксом). Диффы v1↔v2 в
`catalog_versions.section_strings` — ровно по одному токену, всё остальное идентично
байт-в-байт (559=559, 769=769 токенов). **Хэш-движок (FHV18) невиновен — diff был
реальным: второй импорт выполнялся на мини-проекте, а не на живом проекте**
(лог: `Active document is a SmartCon mini-project — ignoring`, `Phase 2: mini-project —
confirmation skipped`).

### 1.2 Механика рождения дубля материала (step-probe в реальном Revit 2025)

Пошаговый прогон staging-последовательности (`NewProjectDocument` →
`TemplateCollisionResolver` → `CopyElements` → `Pipe.Create` → normalize) с дампом
после каждого шага:

- Дубль рождается **внутри `CopyElements`** (шаг 3); размещение/нормализация ничего
  не меняют. Воспроизводится на чистом английском шаблоне без KAN-материала.
- Одно имя материала приходит **двумя путями** за один проход:
  1. **Файловая внутренняя запись** материала .rfa-пакета фитинга (routing-зависимости
     тащит CopyElements автоматически) — ранний id (напр. 2967), после копии —
     **полный сирота (0 referrers, подтверждено полным сканом GetMaterialIds +
     EditFamily) → удаляема**;
  2. **Проектный материал сегмента** — поздний id (напр. 12330), получает суффикс
     `'1'`; на него ссылаются сегмент (`Segment.MaterialId`) и material-параметры
     типов фитингов (`BP_CaseMaterial`).
- **Правило дубля:** дубль рождается IFF имя материала сегмента совпадает с
  family-internal записью одного из routing-фитингов (KAN-кейс) ИЛИ с материалом
  дефолтного шаблона (`Медь` в RU-шаблоне). Нет совпадения — нет дубля (ПЭ-кейс:
  фитинги несут `ADSK_Полиэтилен`, сегмент — `Полиэтилен PE-X` → чисто, реимпорт =
  Duplicate ✓). Ручной копипаст инстанса трубы — тоже чисто (другая dependency-цепочка).
- **Платформенная причина:** Revit ≤2022 мержил/перезаписывал одноимённые материалы
  при копировании; Revit 2024+ плодит `'Name1'` (Autodesk forum, июнь 2025:
  «I didn't find a way to overwrite at the moment»). API-хука для материалов НЕТ
  (`IDuplicateTypeNamesHandler` покрывает только имена типов). Поэтому баг есть и
  на `develop` — это не регрессия FHV-волны.
- `Segment.MaterialId` — **read-only** (revitapidocs 2025-2027; Micrographics:
  «to change the material, create a copy of the segment»). Перенацелить сегмент
  на другой материал нельзя; пересозданный сегмент не привязать к типу (связь
  тип↔сегмент живёт в routing preferences).

### 1.3 RoutingPreferenceManager — НЕ read-only (опровергнуто старое допущение)

ADR-061 фиксировал «Revit не умеет перезаписывать маршрутизацию» (wishlist 2016).
Это устарело: `SystemTypeSyncService.SyncRoutingPreferences` уже использует
`manager.RemoveRule(group, i)` + `new RoutingPreferenceRule(partId, description)`
+ `rule.AddCriterion(PrimarySizeCriterion)` + `manager.AddRule(group, rule)`
(`SystemTypeSyncService.cs:286-414`). **Правила трассировки полностью перезаписываемы
через API.** Это снимает последнее обоснование для CopyElements в staging.

### 1.4 Фитинги в мини-проекте не нужны (доказано)

- Sync берёт фитинги **из каталога**, не из мини-проекта:
  `CatalogFittingDependencyResolver.EnsureFitting` → `family_dependencies` links →
  managed .rfa (`ResolveForLoadAsync`) → LoadFamily вне транзакции
  (`CatalogFittingDependencyResolver.cs:70-130`).
- Фитинги в мини-проекте служат только: (а) физическому существованию routing-правил
  в Revit-форме (правило требует валидный partId в документе), (б) автосбору
  «толпы зависимостей» при переимпорте (генератор фантомов и обрезков).

### 1.5 Обрезки семейств (подтверждено владельцем и кодом)

`RevitFamilyDependencyCollector` резолвит rule part-токены `'Family:Type'` в семейства
**проекта**; EditFamily захватывает только типы, загруженные в проект; CopyElements в
мини-проект тащит только rule-referenced символы. Итог: каталог получает
проект-loaded подмножество («обрезок»), мини-проект — ещё меньший. Пример:
`ADSK_Разделитель трубопроводов` в живом проекте имеет типы per хост-труба
(`Водогазопроводная труба`, ...), в ПЭ-мини — один тип `Полимерная труба`.
**Разделитель — отдельный класс:** его контент легитимно контекстно-зависим
(per хост-труба) → Existing между live- и мини-контекстом корректен, это НЕ
материальный баг. Полные семейства (все типы) приходят только файловым
batch-импортом .rfa.

### 1.6 Прочие факты

- `PartType` хранится в БД: FamilyFacts (ADR-055), факт `part_type` из
  `FAMILY_CONTENT_PART_TYPE` для pipe/duct/cable-tray/conduit fittings,
  `PartTypeLabelMap` по enum Revit 2025. Доступно для фильтрации в редакторе.
- Дефолт «Пропустить» для routing-зависимости со статусом Existing — by design
  (ADR-066, `FamilyManagerMainViewModel.Import.cs:158-172`): системный родитель
  резолвит детей динамически по активной версии. Не баг.
- Реимпорт мини→мини стабилен (v2→v3 = Duplicate): рестейджинг не наращивает
  суффиксы. Фантом возникает только на стыке контекстов live↔мини.
- Мини-проект остаётся открытым/активным документом → пользователь легко запускает
  «Импорт активного файла» на нём, не замечая (UX-фактор, отдельный гейт).

### 1.7 Сегменты и материалы — только у труб (probe 2026-08-28, тезис владельца)

Probe `DuctSegmentReality` на дефолтном шаблоне (Revit 2025 EN):
- Все `Segment`-элементы — только `PipeSegment` (12 шт, с материалами); класса
  `DuctSegment` в публичном API **не существует**; скан всех элементов по имени
  класса `*Segment*` не нашёл ничего воздуховодного.
- У шаблонного `DuctType` routing-менеджер **полностью пуст** (0 правил во всех
  группах, включая Segments) — и это валидное состояние.
- У лотков/коробов сегментов нет by design; у flex — routing null.
**Вывод для per-category стратегии:** сегментная машинерия
(`PipeSegment.Create`, материал, спецификация, sizes) нужна **только для труб**
(pipe/flex pipe). Для воздуховодов/лотков/коробов/гибких ручной staging
тривиален: типы + WriteParameters + routing-из-БД — нечему ни дублироваться, ни
расходиться. Остаточный edge: если в каком-то проекте есть UI-созданные
duct-сегменты (HVAC-шаблоны; не верифицировано — их класс/материал), создание
через API невозможно → accepted not-converged + Warn (зафиксировать в P0.5).

### 1.8 Вердикт независимого валидатора (general-субагент, 2026-08-28)

План признан осуществимым и прочным по ядру (все нужные API в production с
Revit 2013 на всех конфигах R19-R25; наследование routing при `Duplicate()`
доказано тестами `SystemTypeMepSyncTests.cs:89-95`; sync-машинерия reusable;
материал-дубль устраняется конструктивно; ADR-054/063/066/067 не ломаются) —
**с 8 обязательными поправками**, учтёнными ниже (фазировка Ф1+Ф2-core как один
релизный юнит, legacy-fallback, links новых версий, per-type hashes редактора,
расширенный P0.2, ослабленный D5, file-free backfill, мелочи).

## 2. Decision

### 2.1 Полный отказ от CopyElements в staging MEPCurve — ручное создание (как sync)

Staging строит мини-проект вручную, переиспользуя sync-машинерию (ADR-061), —
**ничего не копируется**:

1. `NewProjectDocument(UnitSystem.Metric)` (как сейчас).
2. Для каждого типа: `prototype.Duplicate(sourceName)` (прототип — тип той же
   системной семьи из шаблона; коллизии имён — существующий
   `TemplateCollisionResolver`) → `WriteParameters` из sync (поштучная запись
   типовых параметров из снапшота источника; экземплярные параметры не переносятся
   никуда — не интересуют).
3. Сегменты: `PipeSegment.Create` + find-or-create материал по имени
   (`RevitMaterialSyncService`) + `PipeScheduleType.Create`/find + схождение sizes
   (`RevitSegmentSyncService` — те же билдеры, что и у hash extraction, FHV4-принцип
   «hash reflects exactly what sync writes»).
4. Routing в мини-проекте: `Duplicate()` наследует правила прототипа →
   **fitting-группы очищаются `RemoveRule`**, в группу Segments пишется правило на
   созданный сегмент (`AddRule`) — тип валиден (пустая Segments-группа недопустима),
   фитингов в мини-проекте НЕТ.
5. Размещение инстансов — как сейчас (`Pipe.Create` и т.п., уже ручное).
6. ES-маркер мини-проекта (#188) — без изменений.

### 2.2 Материалы — find-or-create по имени, дубль не рождается by construction

Единственная точка входа материалов в мини-проект — find-or-create по имени
(сегменты) + шаблон (hash-невидим). Ни семейства фитингов, ни их family-internal
записи в мини-проект не попадают → двух путей для одного имени не существует →
класс дублей устранён структурно. Никаких нормализаторов/ренеймов не требуется.

### 2.3 Routing = данные каталога (не Revit-форма в мини-проекте)

- Новая таблица `family_routing_rules` (catalog_item, version_label, group_type,
  rule_order, part → catalog_item/type link (+ имя-токен fallback), min, max,
  description) — правила версионируются вместе с итемом.
- При первичном импорте из живого проекта routing-снапшот читается из живого
  менеджера (read-only) и пишется в таблицу; part-токены мапятся на catalog items
  по normalized family name (несуществующие → флаг missing для редактора).
- **ROUTING-секция хэша:** формат НЕ меняется (те же `'Family:Type'`,
  PrimarySizeCriterion, порядок) → FHV-бамп не нужен. Источник секции:
  живой менеджер при импорте из проекта; БД — для мини-проекта/каталога.
  Семантика дедупа: редактор не тронут → реимпорт из проекта = Duplicate;
  редактор изменил правила → реимпорт из проекта = легитимный Existing (каталог
  осознанно расходится с проектом).
- Sync читает routing-снапшот **из БД** (не из мини-проекта); сегменты/типы — из
  мини-проекта (физически присутствуют). `EnsureFitting` без изменений.

### 2.4 Редактор трассировки в свойствах итема (зеркало диалога Revit)

Вкладка у системного итема MEPCurve (**только для MEP-категорий** — решение
владельца): группы `RoutingPreferenceRuleGroupType`
(Сегмент трубы [read-only отображение сегментов итема], Отвод, Тройник, Крестовина,
Переход, Соединение, Фланец, Заглушка + аналоги duct/tray/conduit), несколько правил
на группу с критериями мин./макс. размер («Все»), порядок правил (↑↓),
PreferredJunctionType, «Нет» (пустое правило, InvalidElementId — легально).

**Killer feature (владелец, 2026-08-28):** деталь выбирается **из всего каталога** —
и это выбор **семейство + ТИП** (как в диалоге Revit: `'BP_A0301_KAN-therm_Inox:
BP_A0301_KAN-therm_Inox'`), не просто семейство. Фильтр по `part_type`
(FamilyFacts) + категории сужает список до релевантных деталей; вся база фитингов
доступна для компоновки трассировки. Мини-проекты становятся лёгкими (без фитингов —
меньше места на диске), а routing компонуется из полноценных каталожных семейств.

**Динамические link-индикаторы (владелец):** каталог показывает, какие семейства
выбраны в настройках трассировки итема (существующая визуализация
`family_dependencies`-связей) — и это обязано быть **динамическим**: правка routing
в редакторе немедленно обновляет связи и их отображение. Редактор обновляет
`family_dependencies` (V30) → drift-badge при выходе новой версии фитинга
продолжает работать.

Правка routing = новая версия итема (ROUTING в хэше); presence-индикатор
(«нет в каталоге → импортируйте .rfa»).

### 2.5 Фитинги в каталог — файловым batch-импортом полных .rfa; автосбор СОХРАНЯЕТСЯ

Конец эпохи обрезков: канонический источник полноценных семейств (все типы) —
файловый batch-импорт .rfa. **Автосбор routing-зависимостей НЕ удаляется**
(решение владельца, D1): это канальный способ пакетно импортировать все фитинги
трассы в базу из живого проекта. Он структурно перестаёт срабатывать при
реимпорте **из мини-проекта** (там нет ни правил, ни фитингов) — «толпа из 18
строк» исчезает именно в проблемном сценарии, оставаясь доступной при импорте
из живого проекта.

### 2.6 Scope и per-category стратегия (probe-verified §1.7)

| Категория | Сегменты/материал | Стратегия staging |
|---|---|---|
| pipe, flex pipe | PipeSegment + материал + спецификация + sizes (**у flex сегментов нет** — непрерывная геометрия) | **Full manual** (pipe); flex — как «тривиальный» ниже, но с учётом примечания о routing |
| duct | сегментов/материала нет (§1.7; edge: UI duct-сегменты HVAC-шаблонов — accepted not-converged + Warn) | **Тривиальный manual**: типы + WriteParameters + routing-из-БД |
| cable tray, conduit | нет by design | Тривиальный manual |
| flex pipe/duct | сегментов нет; routing **почти полный** (скрины владельца 2026-08-28: PreferredJunctionType, Тройник, Врезка, Переходный, 3 мульти-форма перехода, Соединение и т.д. — без Segments) | Тривиальный manual + routing-из-БД |

**Flex-уточнение (2026-08-28, владелец по скринам + Autodesk REVIT-76496):**
`FlexPipeType`/`FlexDuctType` — `MEPCurveType`, routing-менеджер наследуют.
**Набор групп у flex ПОЧТИ ПОЛНЫЙ** (скрины свойств типа владельца:
Предпочтительный тип соединения = PreferredJunctionType; Тройник = Junctions;
Врезка; Переходный = Transitions; «Переход переменного сечения:
прямоугольный→круглый/овальный, овальный→круглый» =
TransitionsRectangularToRound=7 / RectangularToOval=8 / OvalToRound=9;
Соединение = Unions; и т.д.) — отсутствует фактически только Segments
(у flex непрерывная геометрия). UI flex рендерит настройки **строками прямо в
свойствах типа** (группа «Соединительные детали»), а не отдельным диалогом
«Параметры трассировки» как у жёстких — но бэкенд тот же
RoutingPreferenceManager: built-in параметры `RBS_CURVETYPE_DEFAULT_*`
**не используются с Revit 2013** (Autodesk REVIT-76496 «Works As Expected» —
прямое чтение этих параметров возвращает протухшие значения, ходить надо через
менеджер). Наш extractor читает routing для ЛЮБОГО `MEPCurveType`
(`ExtractRoutingPreferences:2251-2298`) → flex-правила извлекаются и хэшируются.
**Редактор category-aware (уточнено):** для flex — все группы КРОМЕ Segments;
для pipe/duct — полный набор; `AddRule` валидирует совместимость детали с
группой (revitapidocs AddRule Exceptions) → фильтр part_type редактора обязан
мапить группа↔part_type.
**⚠ P0.2-риск (записать в аудит):** deprecated-параметры
`RBS_CURVETYPE_DEFAULT_TEE/CROSS/...` физически могут присутствовать на
flex-типах с протухшими ElementId-значениями — P0.2 обязан проверить, что они в
  EXCLUDED-наборе VALUES (или добавить), иначе хэш потащит протухшие ссылки, а
  ручной staging/WriteParameters попытается их резолвить по имени.

**Дисклеймер точности для будущих агентов:** ранняя редакция этого ADR
ошибочно фиксировала «flex routing = только Elbows+Junctions» — это неверно
(опровергнуто скринами владельца 2026-08-28: набор почти полный, без Segments).
Опираться только на текст выше.

Non-MEP (стены/полы/крыши/потолки/лестницы/ограждения/провода) — CopyElements пока
остаётся (нет routing-протаскивания; материал-сплит для compound layers не
наблюдался) — отдельная оценка в Фазе 4 (включая insulation/lining — у них тот
же класс риска материал-параметра, конверсия тривиальна).

## 3. План по фазам

### Фаза 0 — probe-валидация (integration, disposable; ~0.5-1 день)

- **P0.1** Ручной staging KAN-трубы end-to-end: секции мини vs live токен-в-токен
  (VALUES/TYPES/SEGMENTS паритет); ровно один материал на имя; сегмент читает чистое
  имя. Сиды — на базе временного `MiniProjectMaterialProbeTests.cs` (удалить после Ф0).
- **P0.2** Аудит hash-included типовых параметров по ТРЁМ осям (валидатор):
  (а) writability — included-but-unwritable с контекстно-зависимым значением =
  блокер Ф1; (б) **presence** — project/shared-параметры, прибинденные к
  категории в живом проекте, отсутствуют в шаблонном мини → токен VALUES
  исчезает (класс существует и у CopyElements сегодня — 559=559 в KAN-кейсе был
  чист, но в общем случае нет); (в) **ElementId-name resolvability** — параметры,
  ссылающиеся не на материалы/подтипы. Локаль имён параметров — фиксируем как
  pre-existing limitation (#191), не регрессия. Инструмент: лог «8 included,
  19 excluded» (`RevitFamilySnapshotExtractor.cs:2126-2135`). Included-but-
  unwritable → исключение из хэша (FHV19 + critical actualization) или маппинг.
- **P0.3** Prototype availability — конфирмация инварианта (владелец: последний
  тип системной семьи неудаляем → прототип есть всегда). Покрытие: duct
  (3 семьи: Round/Rect/Oval — `SystemFamilyKeyResolver.cs:58-63`), conduit (2),
  cable tray (2), lining, insulation, wire. Guard `FamilyNotFound → skip+Warn`
  (`SystemTypeSyncService.cs:156-171`) **обязателен** и в staging (не fatal).
- **P0.4** `Duplicate()` наследует routing прототипа — **уже доказано тестами**
  (`SystemTypeMepSyncTests.cs:89-95` — после `pipeType.Duplicate` тест удаляет
  унаследованные правила циклом `GetNumberOfRules → RemoveRule`; валидность типа
  с единственным Segments-правилом и пустыми fitting-группами — :123,:162-163).
  Осталось проверить: стабильность после SaveAs/reopen; `AddRule` Segments на
  созданный сегмент.
- **P0.5** Экстракция SEGMENTS из ручного мини == live (имя материала, порядок
  sizes, roughness, спецификация) + edge-кейсы (валидатор): сегмент с пустой
  size-таблицей (создание невозможно — `RevitSegmentSyncService.cs:111-117`),
  UI duct-сегменты HVAC-шаблонов (§1.7 — accepted not-converged + Warn).
- **P0.6 — СНЯТ:** отдельный probe не нужен. Набор групп flex подтверждён
  скринами владельца (почти полный, без Segments — см. «Flex-уточнение»);
  deprecated-параметры flex вынесены в аудит P0.2. Таблица группа↔part_type
  для фильтров редактора составляется статически из
  `RoutingPreferenceRuleGroupType` (revitapidocs 2025: Segments=0, Elbows=1,
  Junctions=2, Crosses=3, Transitions=4, Unions=5, MechanicalJoints=6,
  TransitionsRectangularToRound=7, TransitionsRectangularToOval=8,
  TransitionsOvalToRound=9, Caps=10) и `PartTypeLabelMap` (FamilyFacts).

### Фаза 1+2 — ОДИН РЕЛИЗНЫЙ ЮНИТ: ручной staging + routing как данные (~4-6 дней)

**Блокер фазировки (валидатор, обязательно):** Ф1 и ядро Ф2 неделимы —
(а) после Ф1 мини содержит только Segments-правило → sync, читающий routing из
мини (Ф2 не shipped), сочтёт fitting-группы цели «target-only» и **очистит их в
проектах пользователей** (деструктивная регрессия); (б) stored v1 ROUTING =
полные правила (из live), мини ROUTING = Segments-only → reimport-from-mini =
вечный «Существующее». Поэтому единый юнит:

1. `ManualSystemStagingService` (SmartCon.Revit): reuse `WriteParameters`,
   `RevitMaterialSyncService`, `RevitSegmentSyncService`,
   `TemplateCollisionResolver`, placement-хэндлеры `SystemCategoryRegistry`,
   маркер `RevitMiniProjectMarker`; guard `FamilyNotFound → skip+Warn`.
2. Таблица `family_routing_rules` (миграция V34+, additive → min_plugin_version
   НЕ бампать, прецедент V29) + репозиторий (образец
   `LocalFamilyDependencyRepository`); запись при импорте из живого проекта.
3. Sync читает routing-снапшот **из БД** — точка переключения одна
   (`template.Routing`, `SystemTypeSyncService.cs:133`); **legacy-fallback
   (обязателен): нет строк routing для активной версии → читать из мини
   (текущее поведение)** — иначе sync старых эталонов сотрёт fitting-правила
   (пустой снапшот → все группы «target-only» → RemoveRule).
4. Reimport-from-mini: подмена `snapshot.Types[].Routing` из БД в
   `FamilyImportPreparationService.PrepareSystemCategoryAsync` (:1206-1218,
   до `ComputeForSystem`); mini-marker + `ReadCatalogItemId` доступны в том же
   Revit-thread roundtrip (ADR-062).
5. **Links новых версий (забытое место, валидатор):** `family_dependencies`
   version-scoped (DELETE per parent_version_id) → reimport-from-mini с пустым
   collector = новая версия БЕЗ links (guard/скрепка/drift деградируют до
   name-fallback). Нужна генерация links из `family_routing_rules`
   (part→item маппинг), не только из collector.
6. Ветка `CreateCleanProjectWithTypesAndInstances` для MEPCurve заменяется;
   non-MEP пока на CopyElements.

Тесты (integration): KAN (нет дублей, дедуп), ПЭ (регрессия), reimport-from-mini
= Duplicate, reimport-from-live = Duplicate, sync в пустой проект, multi-type
труба, legacy-fallback (старая версия без routing-строк → sync не деструктивен),
links новой версии из routing-таблицы. Gate: валидация субагентом `general` +
полные сьюты R25 + net48.

### Фаза 2b — actualization (лечение существующих баз)

- **Backfill routing — file-free (валидатор):** первичный источник НЕ мини-файлы,
  а `catalog_versions.section_strings` (V33, ADR-071) — парсинг ROUTING-секции +
  маппинг part→item по normalized name (`RunFileFreePassAsync`); открытие мини —
  fallback для версий без section_strings.
- **Детект — tracking-колонка** (как `es_marker_version` у
  `MiniProjectMarkerActualizationTask`), НЕ отсутствие строк (легитимно пустой
  routing у flex). Класс задачи: optional (образец
  `MiniProjectMarkerActualizationTask` — `SqlDetectionActualizationTaskBase`,
  `RequiresExtraction => false`).
- **Slimming старых мини** (в той же задаче или отдельной): open → clear
  read-only → RemoveRule fitting-групп → delete протащенные фитинги → delete
  orphan-материалы → rename суффиксных рабочих копий → save in-place → удаление
  `name.NNNN.rvt` backup'ов → restore read-only (прецедент
  `RevitMiniProjectActualizationService`, I-16 exception).

### Фаза 3 — редактор трассировки + динамические links (~3-5 дней)

UI-вкладка (модель — скрин диалога Revit от владельца 2026-08-28), **только для
MEP-категорий**; выбор семейство+тип из всего каталога с фильтром part_type;
**все группы `RoutingPreferenceRuleGroupType`** (валидатор: accessories и пр.
покрываются обобщённо — `SystemTypeSyncService.cs:397-411`); presence-индикаторы;
versioning при правке; обновление `family_dependencies` и **динамическое
отображение routing-связей в каталоге** (правка → репозиторий + триггер
`LoadTreeAsync`/точечный `AttachDependencyIndicatorsAsync` — паттерн инвалидации
уже есть, `Tree.cs:739-774`; малая проводка, не новая подсистема);
**пересчёт per-type hashes новой версии** (валидатор: правка routing меняет
per-type хэши затронутых типов → per-type stale-точки #253, иначе карта stale
соврёт); UX-подсказка: фитинг, убранный из routing, остаётся залочен пока живы
архивные версии с ним (ADR-067 — иначе саппорт-кейс «я удалил из routing, а
семейство не удаляется»). Автосбор из живого проекта сохраняется (D1);
разделитель-класс уходит из жизни пользователя (больше не перетаскивается из
мини-проекта). Ручной тест владельцем + валидация логов (обязательный workflow).

### Фаза 4 — non-MEP системные категории (оценка → решение)

Проверить наличие материал-сплита для compound structure (слои стен vs
family-internal записи profile-семейств). Если подтвердится — тот же ручной
staging (Duplicate + WriteParameters + `RevitCompoundStructureSyncService`),
иначе — оставить CopyElements.

### Фаза 5 — UX-гейт импорта на мини-проекте

«Импорт активного файла» при активном managed мини-проекте — явное
предупреждение/выбор (сейчас «confirmation skipped» молча). Актуально независимо
от рефакторинга.

## 4. Consequences

**Плюсы:** класс материал-дублей устранён структурно (не workaround); конец
обрезкам семейств; конец фантомным diff на стыке контекстов; batch-диалог чистый;
независимость от поведения CopyElements (Autodesk уже менял его в 2024);
routing редактируем централизованно и версионируется; drift-detection фитингов
сохраняется; sync-машинерия переиспользуется (уже battle-tested).

**Минусы/риски и нейтрализация:**
- R1: included-but-unwritable типовые параметры → фантом в VALUES при ручном
  создании. Нейтрализация: P0.2 аудит; при наличии — FHV19 с исключением +
  critical actualization (есть движок ADR-054).
- R2: отсутствие прототипа системной семьи в шаблоне (экзотика) → D5 fallback.
- R3: редактор = заметный UI-объём. Поэтапно: Ф1-Ф2 дают корректность и без
  редактора (routing приезжает из проекта).
- R4: обрезки семейств при автосборе из живого проекта сохраняются как класс
  (D1 — фича осознанно оставлена) — нейтрализация: полные .rfa версионируются
  поверх обрезков; редактор показывает полноту типов.
- R5: legacy-обрезки и загрязнённые версии в существующих базах → D3/D4.
- R6: мини-проект перестаёт быть «самодостаточной моделью» (без фитингов рисование
  сети в нём ограничено) — приемлемо: мини = эталон типов/сегментов, не рабочий проект.

## 5. Открытые решения

- **D1 — РЕШЕНО (владелец, 2026-08-28):** автосбор routing-зависимостей
  **сохраняется** — это канальный путь пакетного импорта фитингов трассы в базу
  из живого проекта. Из мини-проекта он структурно не срабатывает (нет правил и
  фитингов) — толпа в batch-диалоге исчезает именно там. Дополнительно:
  link-индикаторы routing-связей в каталоге обязаны быть динамическими
  (обновляются редактором); вкладка трассировки — только для MEP.
- **D2 — РЕШЕНО (валидатор, подтверждено кодом):** мастер не нужен — автосбор из
  живого проекта и есть «мастер наполнения» (missing-фитинги приезжают New
  dependency-строками с IncrementVersion). После рефакторинга missing-партами
  остаются только заSkip'енные пользователем строки → **presence-флаги в
  редакторе** («нет в каталоге → импортируйте .rfa») достаточны.
- **D3 — РЕШЕНО (владелец):** actualization-slimming — заражённые мини-проекты
  лечатся задачей актуализации (file-free backfill routing + slimming) кнопкой
  «Обновить базу» (Фаза 2b).
- **D4 — РЕШЕНО (валидатор, трасса по коду):** legacy-обрезки остаются версиями;
  полный .rfa создаёт v(N+1) и автоматически становится активной. Links НЕ
  ломаются: `family_dependencies` — item-level (`child_catalog_item_id`), sync
  резолвит активную версию → родитель начнёт тянуть ПОЛНОЕ семейство
  автоматически; смена активной версии ребёнка легитимно поднимает drift-badge
  родителям (feature, не bug). Обрезок удаляется вручную свободно (guard
  item-level, версия ребёнка удаляется без ограничений). Специальная миграция
  не нужна.
- **D5 — РЕШЕНО (владелец + валидатор):** прототип существует всегда (последний
  тип системной семьи неудаляем). Fallback не требуется, но guard
  `FamilyNotFound → skip+Warn` обязателен (не fatal); P0.3 покрывает
  oval duct/lining/insulation/wire.

## 6. Ключевые ссылки на код

- Staging (текущий): `SystemFamilyRevitOperations.CreateCleanProjectWithTypesAndInstances`
  (210-374), `TemplateCollisionResolver`, `SystemCategoryRegistry` (placement).
- Sync-машинерия (reuse): `SystemTypeSyncService` (`Duplicate` :173, `WriteParameters`
  :178, `SyncRoutingPreferences` :286-414, `AddRule` :385), `RevitSegmentSyncService`
  (`PipeSegment.Create` :163, combo-uniqueness material+schedule :174-183),
  `RevitMaterialSyncService`, `RevitCompoundStructureSyncService`.
- Fitting resolution: `CatalogFittingDependencyResolver` (:70-130),
  `IFittingDependencyResolver`.
- Routing-модель: `RoutingPreferencesSnapshot`/`RoutingRuleSnapshot`,
  `RevitFamilySnapshotExtractor` (:2259, :2379 — `ConvertRoutingRule`, канонический
  формат секции), `RoutingPreferenceRuleGroupType`.
- Dependencies: `RevitFamilyDependencyCollector` (:18-106), `DependencyLinkPlanner`,
  `FamilyManagerMainViewModel.Import.cs` (:158-172 — дефолты Action).
- Hash: `FamilyContentHasher.SystemSections.cs` (:179 compound, :228 segment,
  :291 rail, :319 — чтение имён материалов), ADR-071 (секции/FHV).
- PartType: `FamilyFactRuleSet` (:51-91), `PartTypeLabelMap`.
- Маркер мини-проекта: `RevitMiniProjectMarker` (#188, ADR-062).
- Временный probe (удалить после Ф0): `src/SmartCon.IntegrationTests/FamilyManager/MiniProjectMaterialProbeTests.cs`.
