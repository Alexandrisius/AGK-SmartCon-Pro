# SmartCon — SSOT (Single Source of Truth)

> **Версия:** см. `Version.txt` | **Платформа:** Revit 2019-2026 / .NET Framework 4.8 + .NET 8 / C# 12 / WPF
> **Последнее обновление:** 2026-07-08
> **Pre-release:** Поддержка beta-версий через SemVer + GitHub pre-release (ADR-021)

Этот файл — **единая точка входа** в документацию проекта SmartCon.
AI-агент должен загрузить этот файл первым, затем подгружать нужные разделы по контексту задачи.

---

## Что такое SmartCon

SmartCon — плагин для Autodesk Revit, автоматизирующий рутинные MEP-операции.
Флагманский модуль — **PipeConnect**: соединение трубных элементов на любых видах двумя кликами с автоматическим подбором фитингов, параметров и типов соединений.

**Целевой пользователь:** MEP-инженер-проектировщик.

**Ключевая боль:** Revit не умеет удобно соединять элементы в 3D-виде — приходится тягать коннекторы и надеяться на совпадение координат в пространстве. Нет системы типов соединений (резьба/сварка/раструб). Нет умного подбора фитингов-переходников.

---

## Карта документации

Загружай документы по мере необходимости. Колонка «Когда загружать» — подсказка.

### Архитектура

| Документ | Описание | Когда загружать |
|---|---|---|
| [`architecture/solution-structure.md`](architecture/solution-structure.md) | Проекты, папки, файлы каждого слоя | Всегда при создании/перемещении файлов |
| [`architecture/dependency-rule.md`](architecture/dependency-rule.md) | Правило зависимостей между слоями | Всегда |
| [`architecture/dependency-injection.md`](architecture/dependency-injection.md) | DI-контейнер, ServiceRegistrar, Constructor Injection | При добавлении сервиса/ViewModel |
| [`architecture/database-migrations.md`](architecture/database-migrations.md) | Паттерн миграций catalog.db (badge + «Обновить базу» + load gate) | При любом изменении данных/схемы БД FamilyManager |
| [`architecture/tech-stack.md`](architecture/tech-stack.md) | Стек технологий, версии, NuGet-пакеты | При настройке проекта или добавлении зависимостей |

### Домен

| Документ | Описание | Когда загружать |
|---|---|---|
| [`domain/README.md`](domain/README.md) | Индекс доменной документации | При навигации по доменным моделям/интерфейсам |
| [`domain/models/`](domain/models/README.md) | Доменные классы (модели), разбиты по модулям | При работе с моделями данных |
| [`domain/interfaces/`](domain/interfaces/README.md) | Интерфейсы-контракты, разбиты по модулям | При реализации или вызове сервисов |
| [`domain/glossary.md`](domain/glossary.md) | Единый словарь терминов проекта | При любых сомнениях в терминологии |

### PipeConnect (флагманский модуль)

| Документ | Описание | Когда загружать |
|---|---|---|
| [`pipeconnect/README.md`](pipeconnect/README.md) | Индекс модуля, обязательные ADR, инварианты | При любой работе с PipeConnect |
| [`pipeconnect/state-machine.md`](pipeconnect/state-machine.md) | Диаграмма состояний, переходы, кейсы, матрица решений | При работе с логикой PipeConnect |
| [`pipeconnect/algorithms.md`](pipeconnect/algorithms.md) | Алгоритмы: выравнивание, параметры, фитинги, цепочки | При реализации алгоритмов |
| [`pipeconnect/ui-spec.md`](pipeconnect/ui-spec.md) | Спецификация UI: окна, layout, MVVM-паттерны | При работе с UI |

### ProjectManagement (модуль шаринга проектов)

| Документ | Описание | Когда загружать |
|---|---|---|
| [`projectmanagement/README.md`](projectmanagement/README.md) | Индекс модуля, список файлов, ключевые решения | При любой работе с модулем |
| [`projectmanagement/share-algorithm.md`](projectmanagement/share-algorithm.md) | Алгоритм Share: 8 шагов, обработка ошибок, категории очистки | При реализации ShareProjectService |
| [`projectmanagement/ui-spec.md`](projectmanagement/ui-spec.md) | Спецификация UI: ShareSettingsView (4 таба), ShareProgressView | При работе с UI |
| [`projectmanagement/naming-template.md`](projectmanagement/naming-template.md) | Парсер имён файлов: блоки, роли, маппинг статусов, JSON-формат | При реализации FileNameParser |

### FamilyManager (модуль управления семействами)

| Документ | Описание | Когда загружать |
|---|---|---|
| [`family-manager/README.md`](family-manager/README.md) | Индекс модуля, архитектура, таблицы БД, миграции, фазы | При любой работе с FamilyManager |

### FloorHeating (модуль тёплого пола, в разработке)

| Документ | Описание | Когда загружать |
|---|---|---|
| [`floorheating/product-brief.md`](floorheating/product-brief.md) | Что строим и зачем (изменчивый) | При уточнении scope |
| [`floorheating/kernel-spec.md`](floorheating/kernel-spec.md) | Математическая спецификация ядра (стабильная) | При любой работе с ядром |
| [`floorheating/KNOWLEDGE-CAPSULE.md`](floorheating/KNOWLEDGE-CAPSULE.md) | Архив уроков 3 попыток (read-only) | Перед архитектурными решениями |

### Правила и решения

| Документ | Описание | Когда загружать |
|---|---|---|
| [`invariants.md`](invariants.md) | Жёсткие правила I-01..I-17. Нарушение = баг. | **ВСЕГДА** |
| [`multi-version-guide.md`](multi-version-guide.md) | Стандарт multi-version: 10 правил, шаблоны, чеклист | При создании нового функционала |
| [`adr/README.md`](adr/README.md) | Индекс Architecture Decision Records | При вопросах «почему так сделано?» |
| [`known-workarounds.md`](known-workarounds.md) | Реестр workaround'ов (Issue, файл, платформа, статус) | При вопросах «что это за странный код?» |
| [`references.md`](references.md) | Внешние ссылки на документацию Revit API | При работе с конкретными API |

---

## Быстрый старт для AI-агента

1. **Загрузи** этот файл (`docs/README.md`)
2. **Загрузи** [`invariants.md`](invariants.md) — жёсткие правила, обязательные всегда
3. **Загрузи** [`architecture/dependency-rule.md`](architecture/dependency-rule.md) — чтобы понимать куда класть код
4. **По задаче** загружай нужные документы из карты выше
5. **Не создавай** новые доменные классы без обновления [`domain/models/<module>.md`](domain/models/README.md)
6. **Не создавай** новые интерфейсы без обновления [`domain/interfaces/<module>.md`](domain/interfaces/README.md)

---

## Текущий статус

| Модуль | Статус | Примечание |
|---|---|---|
| SmartCon.Core | ✅ Полный | Модели, интерфейсы, алгоритмы, FormulaSolver |
| SmartCon.Revit | ✅ Полный | Все Revit API реализации |
| SmartCon.UI | ✅ Полный | Тема, стили, контролы, конвертеры |
| SmartCon.App | ✅ Полный | Ribbon, DI, ExternalEvents, Updater |
| SmartCon.PipeConnect | ✅ Полный | PipeConnect: 5 partial VM, 12 сервисов, 6 окон |
| SmartCon.ProjectManagement | ✅ Реализован | Share Project: ISO 19650, ADR-013 |
| SmartCon.FamilyManager | ✅ Реализован | FamilyManager: dockable panel, SQLite catalog, Published Storage, ADR-015, Stale Detection v2 (ADR-030), Type Catalog Bake-in (ADR-033) |
| SmartCon.Tests | ✅ 1669 тестов, 0 ошибок | Unit + ViewModel тесты (xUnit + Moq) |

**Phase 11 (ProjectManagement) завершена (2026-04-25):** Share Project, Field Library, FileNameParser с валидацией, 12-категорийная очистка модели, 716 тестов.

**Phase 12 (FamilyManager MVP) завершена (2026-04-28):** ADR-014 принят. Документация обновлена: модели, интерфейсы, структура solution, dependency rule.

**Phase 14 (FamilyManager MVP Architecture v2) завершена (2026-05-03):** ADR-014 принят и заменён ADR-015. Архитектура Published Storage.

**Phase 15 (FamilyManager Published Storage) завершена (2026-05-01):** ADR-015, ADR-016, Published Storage, Asset management, Category tree.

**Phase 16 (FamilyManager ReadOnly Storage) завершена (2026-05-02):** ADR-016, ReadOnly-флаг для managed-файлов, SHA-256 верификация.

**Phase 17 (FamilyManager Attribute Extraction Foundation) завершена (2026-05-06):** ADR-017, AttributeDefinition, CategoryAttributeBinding, EffectiveCategoryAttribute, metadata package import/export, schema v6.

**Phase 18 (FamilyManager Refactoring) завершена (2026-05-07):** ADR-018, DI ViewModel Factory, DialogResult enum, batch GetBindingCountsAsync, async void FireAndForget, StringComparer.Ordinal, Split(char) optimization, I-12 programmatic headers. 1068 тестов.

**Phase 19 (Pre-release Beta Support) завершена (2026-05-17):** ADR-021, SemVersion парсер, `IncludePrerelease` настройка, GitHub pre-release workflow, обновление `release.ps1`/`release.bat`, CI триггеры для beta-тегов.

**Phase 20 (FamilyManager RBAC) завершена (2026-05-16):** ADR-022, Role-Based Access Control для локальных каталогов, DbUser/DbUserRole, Profile dialog, ownership transfer.

**Phase 24 (FamilyManager Stale Detection v2) завершена (2026-06-18):** ADR-030, on-demand stale detection через `SmartCon_FamilyVersion_v1` ExtensibleStorage Schema на `Family` элементе в проекте (не на `.rfa` — over-engineered, см. ADR-030 §2), override ADR-014 §FM-007 (см. [ADR-030](adr/030-phase-24-stale-detection-v2.md) и [план реализации](family-manager/02-plans/phase-24-stale-detection-v2.md)). Schema v12: drop table `project_usage` (clean slate, breaking change 2.0.0). ПКМ "Проверить" на категории/семействе, пакетное обновление, roll-up индикация на категориях.

**Phase 25 (FamilyManager Type Catalog Simulation — Issue #66) завершена (2026-06-21):** ADR-032, симуляция типов из `.txt` каталога через `Document.Regenerate()` для вычисления формул. Новый сервис `ITypeCatalogValueApplier` (pure C#), единая точка входа `IFamilyDataExtractionService.ExtractFromManagedFile(path, names, ct)`, per-type/per-parameter изоляция ошибок, `__SCAT__` префикс временных типов, encoding detection через UTF.Unknown, `tx.RollBack()` гарантирует неизменность `.rfa`. Поддержка R19/R21/R24/R25. **Superseded by ADR-033** (bake-in заменил simulation для managed `.rfa`).

**Phase 26 (FamilyManager Type Catalog Bake-in — Issue #74) завершена (2026-06-22):** ADR-033 заменил simulation на **bake-in** — при импорте `.rfa` с `.txt` каталогом типы запекаются прямо в managed storage. `IFamilyTypeCatalogBaker.BakeAsync(sourceRfaPath, catalog, managedRfaPath)` открывает исходный `.rfa` один раз, создаёт все типы из `.txt`, восстанавливает формулы в топологическом порядке, делает `SaveAs` в managed storage. **BAKE-006..009 (commit `19e220e`)**: парсер сохраняет `##TYPE##UNITS` annotation через `TypeCatalogColumn` record, `RevitUnitsCompat.CatalogCellToInternalUnits(raw, annotation, param)` конвертирует mm/cm/in/ft/deg/rad → Revit internal units с валидацией `UnitUtils.IsValidUnit(targetSpec, sourceUnit)`. Pure normalization через `TypeCatalogUnitAlias.Normalize` в `SmartCon.Core` (15 unit-тестов). R21+ использует `FamilyParameter.GetUnitTypeId()` + `SpecTypeId`, R19-R20 — `DisplayUnitType`. Freeze workaround через `RevitBalloonNudge.Nudge` после каждого `Close` (REVIT-236376 / REVIT-237190). Build R19/R21/R25, 1379+ тестов pass.

**Phase 27 (FamilyManager v2.0.0 Cleanup) завершена (2026-06-29):** ADR-034, 035, 036, 037, 038, 039. Shared nested persist fallback, удаление temp-логики, Sync типов, Tree expand/collapse, sticky category headers, snapshot-driven commit. Schema v14: drop SHA-256/size columns. Build R19/R21/R24/R25, 1669 тестов pass.

**Phase 29 (FamilyManager 3D Preview) завершена (2026-07-01):** ADR-042, GLB extraction через SharpGLTF, HelixToolkit.Wpf.SharpDX viewer, `mc:AlternateContent` для net48/net8 совместимости. Build R19/R21/R24/R25.

**Phase 30 (FamilyManager Avatar Crop — Issue #131) завершена (2026-07-16):** ADR-047. Универсальный диалог кадрирования аватарки (рамка 4:3 + zoom/pan + затемнение) из ★/«Сменить»/первичной загрузки изображения. Производный `avatar.png` 560×420 на семейство с инвалидацией при смене primary; единое превью 280×210 в свойствах и tooltip через `GetAvatarImagePathAsync` (avatar.png → primary image). Pure math `CropViewportMath` в Core, WPF-free `CropAvatarViewModel`, `WpfAvatarCropService` с капом декода 4096px. Build R19/R21/R24/R25, 1900 тестов pass.

**Phase 31 (FamilyManager Batch Import Live Progress — Issue #127) завершена (2026-07-16):** ADR-048. Batch-диалог стал modeless (`IDialogPresenter.ShowModeless`) и не закрывается при Import: живой прогресс «X из Y» по строкам (иконки Check/Close/TimerSand), пауза «Остановить» → «Продолжить»/«Закрыть» (`PauseGate`), summary-экран, X во время импорта = пауза. Поэлементный pipeline stage → import → extract через `IFamilyBatchImportExecutor` (pure C#, юнит-тестируемый) + Revit-bound staging за швом `IFileFamilyStagingService`/`IProjectFamilyStagingService` для UC-1 и UC-3/UC-4. Оркестраторы и `ImportBatchAsync` не изменены (per-item вызовы). Build R19/R21/R24/R25, 1940 тестов pass.

**Phase 32 (FamilyManager Content Hash v2 — Issue #126) завершена (2026-07-17):** ADR-049, ADR-050. Rename-invariant дедупликация: имя исключено из loadable canonical string (`FHV2`), дедуп стал hash-first (поиск по всем версиям каталога независимо от имени, индекс `ix_catalog_versions_content_hash`), cross-name дубликаты показывают ⚠ с tooltip. Имя айтема следует за именем файла активной версии (`SetActiveVersionAsync` обновляет `name`+`normalized_name`). Миграция БД пользователей — user-initiated data repair с modeless прогресс-диалогом (ADR-048 паттерн): system families — мгновенный UPDATE флага, loadable — open→extract→close по одному файлу, один файл на version_label для всех Revit-вариантов, chunked commits по 10 файлов (I-14, без WAL), маркировка `-1` для нечитаемых, purge недоступных по подтверждению, фильтр по версии Revit. Precomputer получил `forcedCatalogItemId` для hash-matched строк; staging пропускает MakeActive (фикс orphan SaveAs). Breaking change 3.0.0 без DDL-миграции. 11 новых тестов миграции + обновлённые hasher/dedup/versions тесты.

**Phase 33 (Database Actualization Engine — ADR-054) завершена (2026-07-21):** фреймворк миграций переработан в **единый движок актуализации БД**: задачи `IDatabaseActualizationTask` (`hash-v2` critical, `attributes-v1`/`glb-v1` optional) вместо миграций-стадий. «Хэш устарел / нет атрибутов / нет 3D» — единая модель «не хватает артефактов из файла»: движок union'ит детекты задач (SQL по «пустоте колонок»), открывает каждую pending-семью **ровно один раз** (snapshot+геометрия в одной сессии) и применяет только pending-задачи. UX: ОДНА команда «Обновить базу» (critical — все роли, optional — Owner/BimMaster) → ОДИН modeless диалог с единым прогрессом и сводкой + purge. Critical pending → баннер + красная точка + read-only гейт. Движок чинит хвост #151/#152/#153 (READERROR, unit_type_id NULL, 0 типов) без переимпорта. Новая фича со старыми данными = один класс-задача + строка в DI. ADR-050 помечен superseded (механика hash-задачи сохранена). Инструмент ручного теста: `tools/damage-catalog-db.ps1`. Build R19/R21/R24/R25, 2094 теста pass.

**Phase 34 (Content Hash v3 — Issue #159) завершена (2026-07-23):** ADR-056. FHV3 закрывает ложные дубликаты FHV2: категория — локале-инвариантный ordinal (RU/EN Revit дают один хэш); новые секции FACTS (Part Type — «Отвод»≠«Тройник»), FLAGS (Shared/WorkPlaneBased/AlwaysVertical/CutWithVoids), CONN (коннекторы: domain/профиль/размеры/SystemClassification/Origin 1e-4 ft/linked-связи), STRUCT (слои CompoundStructure для стен/перекрытий/крыш/потолков), ROUTING (правила трассировки PipeType/DuctType/CableTrayType/ConduitType + PreferredJunctionType); геометрия + BoundingBox/SurfaceArea/длины 2D-кривых; non-shared nested; экранирование `|`; blank-маркеры сужены по storage type. Критическая задача `hash-v3` (заменила `hash-v2`): system-группы — полный пересчёт из staged `.rvt` с trim типов до `family_types` (file-free re-flag невозможен). Детект покрывает v1/v2/NULL → v3. Build R19/R21/R24/R25, 2265 тестов pass.

**Phase 35 (FamilyManager #180 + единый хэш FHV10) завершена (2026-08-12):** ADR-068 §A1. «Проверить» стала контентной (вариант B #180): нет маркера → доказательство контентом + heal маркера; маркер устарел → stale без открытий; маркер совпал → пере-проверка, локальные правки = новый `StaleReason.ContentDrift` («Обновить» возвращает каталог). Работает и в семействе-документе (embedded), и в проекте. Два зонда-контракта: группы параметров не переносит НИКАКОЙ merge (вкл. poke + doc-to-doc), а embedded EditFamily-документ НЕ загрязняется хостом (ассоциации живут на экземплярах) — поэтому двухгрейдовая схема упразднена: **единый хэш FHV10** (группы не хэшируются нигде; `ComputeForEmbeddedVerification` удалён). Batch-диалог помечает версии, резолвленные маркером вопреки хэшу («— маркер» + тултип; при FHV10 — спящий индикатор краевых случаев). Критическая задача `hash-v10`, floor `2.0.1-beta.8` (первая бета, выпускающая форматы FHV8+; v8/v9-хэши не шипились). Build R19/R21/R24/R25, unit 2702, интеграционные R25 178/178 + net48 166/11 skip.

**Phase 36 (FamilyManager #210 E3 — кликабельные статус-бэйджи) завершена (2026-08-13):** ADR-066 §6 (E3 as-built). Единый паттерн `StatusNotice` (title + буллет-список имён + guidance) + диалог `StatusDetailsView`: зоопарк из 5 некликабельных значков в batch-диалоге и пассивные бэйджи дерева заменены кликабельными (`StatusBadgeButton`, Generic.xaml) с разделением на два диалога — problem (warning/error + действия) и info (связи, без действий). Действия обновления — сплит-кнопка «Обновить ▾» по канону DbTools (ToggleButton⇄Popup; стили `ModernContextMenu`/`ModernMenuItem`/`SecondaryToggleButton` подняты в Generic.xaml) с CanExecute-гардами команд. Presence-точка типа = третий способ размещения (`PlaceTypeFromIndicator` → selection-independent `PlaceTypeCoreAsync`; оранжевая = обновить+разместить со сторожем «никогда не размещать stale»; #221 — включая безтиповые семейства через виртуальный сентинел). DnD stale-типа перезагружает из каталога (`FamilyPlacementDragData.IsStaleInProject`). Интеграционный wall-тест переработан в DecisiveOutcome (ADR-068 §A2 — «стена» raw reload'а group-only-diff специфична). Ручной тест владельца пройден. Build R19/R21/R24/R25 0/0, unit 2725, интеграционные R25 178/178.


**Phase 37 (FamilyManager #254 — routing как данные каталога + FHV21) ЗАВЕРШЁН (2026-09-01):** ADR-072 (Фазы 0-3), ADR-073. Ручной staging мини-проектов MEPCurve без CopyElements (дубли материалов Revit 2024+), routing — данные каталога: item-level связи фитингов (V37, World B, вне хэша) редактируются вкладкой «Трассировка» в свойствах семейства (multi-rule группы pipe/duct с критериями размеров, param-строки flex/conduit/tray, preferred junction, пикер с part_type/connector_shape фильтрами — host-биты, required-маска для переходов переменной формы, исключение мультиформенных из обычных «Переходов»). FHV21 (ADR-073): сегментная конфигурация трубы (набор + диапазоны + порядок) — версионный контент мини-проекта: диапазоны в хэше (META FHV11, CurrentVersion=21), per-version таблица `family_segment_rules` (V38), откат версии восстанавливает СВОЮ конфигурацию и живо обновляет вкладку, строка «Сегменты» — read-only view реальных критериев. Миграции `hash-v21` (critical) + `segment-rules-v1` (backfill). Локализация форм коннекторов (`ConnectorShapeLabelMap`: «Круглый и Прямоугольный», «Нет коннекторов» для маски 0), перенос строки фактов шапки (WrapPanel). Два стресс-теста владельца: 12 багов исправлены (no-op save, мгновенный stale-пересчёт, пикер по preferred junction, корзина-иконки, «из каталога», shared-параметры). Build R19/R21/R25 0/0, unit 3149, интеграционные R25 238/250 (12 skip — библиотека владельца) + net48 236/249 (13 skip).
