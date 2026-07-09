# ADR-045: Core FileNameParser reused by FamilyManager BaseType

**Date:** 2026-07-09  
**Status:** accepted  
**Related:** Issue #119, ADR-013 (ProjectManagement module), ADR-014/015/018 (FamilyManager architecture)

## Context

FamilyManager ввёл понятие **базы проекта** (Issue #119): база семейств, которая
автоматически активируется, когда имя файла открытого Revit-проекта подходит под
заданный пользователем шаблон. Для этого нужен движок блочного парсинга имён файлов
и валидации полей.

Тот же движок уже существует в модуле ProjectManagement (ShareProject): класс
`FileNameParser`, модели `FileNameTemplate`, `FileBlockDefinition`, `ParseRule`,
`FieldDefinition`. Изначально он был написан в `SmartCon.ProjectManagement`, но его
логика чисто C# и не зависит от Revit API или WPF.

## Decision

**Переиспользовать существующий Core-движок парсинга имён файлов для функции
FamilyManager BaseType.**

Конкретно:

- `FileNameParser`, `IFileNameParser`, `FileNameTemplate`, `FileBlockDefinition`,
  `ParseRule`, `FieldDefinition`, `ValidationMode`, `ProjectBaseMatch` и связанные
  модели остаются в `SmartCon.Core` и используются обоими модулями.
- `BaseType` и `ProjectBaseBinding` добавлены в `SmartCon.Core/Models/FamilyManager`.
- `IProjectBaseBindingEvaluator` и `IProjectBaseActivator` — контракты в Core,
  реализации в Core (`SmartCon.Core.Services.Implementation`).
- UI редактора правил (`ProjectBaseRulesEditorView`) создаётся **отдельно** в
  `SmartCon.FamilyManager`, не переиспользуется `ParseRuleView` из
  `SmartCon.ProjectManagement`.

## Consequences

### Positive

- `SmartCon.FamilyManager` **не ссылается** на `SmartCon.ProjectManagement`.
    Соблюдается `dependency-rule.md` и чистая архитектура ADR-001.
- Единая семантика парсинга и валидации для ShareProject и FamilyManager.
    Исправления/улучшения движка применяются сразу в обоих модулях.
- Движок покрыт ~60 unit-тестами (`FileNameParserTests`), которые продолжают
    защищать новый функционал FamilyManager.
- Автоактивация базы проекта — pure C#, без ExternalEvent, т.к. `SwitchDatabaseAsync`
    работает только с JSON+SQLite.

### Negative / Trade-offs

- UI редактора правил придётся поддерживать в двух местах. Это осознанный trade-off
    ради независимости модулей и разных UX-контекстов (ShareProject export vs
    FamilyManager project base binding).
- Нельзя использовать `ExportMapping` из `FileNameTemplate` для matching — это
    специфичная для ShareProject трансформация, не применяется к привязке базы.

## Alternatives considered

| Альтернатива | Почему отвергнута |
|---|---|
| Project reference FamilyManager → ProjectManagement | Нарушает `dependency-rule.md`, создаёт циклическую зависимость (ProjectManagement уже зависит от Core). |
| Дублировать движок парсинга в FamilyManager | Дублирование кода, расхождение семантики, двойные тесты. |
| Вынести движок в отдельный проект `SmartCon.FileParsing` | Избыточно: движок generic, хорошо укладывается в Core. |

## Implementation notes

- `IProjectBaseBindingEvaluator.Evaluate` возвращает `ProjectBaseMatch` с тремя
  состояниями: `NotApplicable`, `Match`, `Mismatch`.
- `ProjectBaseActivator` перебирает проектные базы в порядке реестра, при первом
  match переключает активную базу через `IDatabaseManager.SwitchDatabaseAsync`.
  Если не нашлось подходящей — fallback на первую общую базу.
- `ProjectBaseBinding` хранится только в `registry.json`.
  `catalog.db.database_meta.base_type` — только convenience-кеш для RBAC.
- Создан `ProjectBaseRulesEditorView` в `SmartCon.FamilyManager` с собственным
  ViewModel, работающим через `IFileNameParser` из Core.

## Verification

- Сборка R19/R21/R24/R25 — 0 warnings / 0 errors.
- Тесты: `ProjectBaseBindingEvaluatorTests`, `ProjectBaseActivatorTests`, все
  существующие `FileNameParserTests` проходят.
- Manual Revit test: создание/редактирование проектной базы + автоактивация при
  смене документа.
