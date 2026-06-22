# SmartCon — SSOT (Single Source of Truth)

> **Версия:** см. `Version.txt` | **Платформа:** Revit 2019-2026 / .NET Framework 4.8 + .NET 8 / C# 12 / WPF
> **Последнее обновление:** 2026-05-20
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
| [`pipeconnect/state-machine.md`](pipeconnect/state-machine.md) | Диаграмма состояний, переходы, правила | При работе с логикой PipeConnect |
| [`pipeconnect/algorithms.md`](pipeconnect/algorithms.md) | Алгоритмы: выравнивание, параметры, фитинги, цепочки | При реализации алгоритмов |
| [`pipeconnect/ui-spec.md`](pipeconnect/ui-spec.md) | Спецификация UI: окна, layout, MVVM-паттерны | При работе с UI |
| [`pipeconnect/business-cases.md`](pipeconnect/business-cases.md) | Бизнес-кейсы: логика при разных сценариях коннекта, reducer, размеры | При реализации логики соединения |

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
| [`family-manager/README.md`](family-manager/README.md) | Индекс модуля, архитектура, таблицы БД | При любой работе с FamilyManager |
| [`family-manager/00-strategy/02-familymanager-systemfamilies-case.md`](family-manager/00-strategy/02-familymanager-systemfamilies-case.md) | Концепция System Families: проект как семейство | При планировании архитектуры модулей |

### Правила и решения

| Документ | Описание | Когда загружать |
|---|---|---|
| [`invariants.md`](invariants.md) | Жёсткие правила I-01..I-17. Нарушение = баг. | **ВСЕГДА** |
| [`multi-version-guide.md`](multi-version-guide.md) | Стандарт multi-version: 10 правил, шаблоны, чеклист | При создании нового функционала |
| [`adr/README.md`](adr/README.md) | Индекс Architecture Decision Records | При вопросах «почему так сделано?» |
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
| SmartCon.Tests | ✅ 1379+ тестов, 0 ошибок | Unit + ViewModel тесты (xUnit + Moq) |

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
