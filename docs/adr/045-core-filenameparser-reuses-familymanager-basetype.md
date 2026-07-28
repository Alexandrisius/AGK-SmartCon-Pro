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
- **Источник истины для `ProjectBaseBinding` — `catalog.db.database_meta.project_binding_json`**, а не `registry.json`. `registry.json` продолжает дублировать binding для runtime-удобства, но при `DisconnectDatabaseAsync` + `ConnectDatabaseAsync` реестр теряется, поэтому binding должен жить в самой базе данных. См. update A1 ниже.

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
- `ProjectBaseBinding` хранится в `catalog.db.database_meta.project_binding_json` как
  источник истины. `registry.json` дублирует binding для runtime-удобства, но при
  `DisconnectDatabaseAsync` + `ConnectDatabaseAsync` реестр пересоздаётся из БД.
  `catalog.db.database_meta.base_type` — convenience-кеш для RBAC и других
  потребителей, которые читают каталог без доступа к реестру.
- Создан `ProjectBaseRulesEditorView` в `SmartCon.FamilyManager` с собственным
  ViewModel, работающим через `IFileNameParser` из Core.
- Migration v21 добавляет колонку `project_binding_json` в `database_meta`.

## Update A1 — source of truth moved from registry to catalog.db (2026-07-09)

Первоначальная версия ADR считала источником истины `registry.json`, а
`database_meta.base_type` — только кешем. Ручное тестирование показало, что при
отвязке проектной базы (`DisconnectDatabaseAsync`) запись из реестра удаляется,
и при последующем подключении как существующей (`ConnectDatabaseAsync`) база
теряла `Kind = Project` и `ProjectBinding`. Для исправления источник истины
перенесён в `catalog.db.database_meta.project_binding_json`, а реестр оставлен
runtime-кешем.

## Update A2 — auto-activation on first Save / SaveAs (2026-07-15)

Ручное тестирование выявило, что при создании нового проекта и его первом
сохранении проектная база не активируется, потому что `ViewActivated` не
срабатывает, когда активный вид не менялся (Issue #128). Для исправления
`ActiveDocumentChangeNotifier` дополнительно подписан на
`ControlledApplication.DocumentSaved` / `DocumentSavedAs` и поднимает
`ActiveDocumentPathChanged`, когда активный документ получает или меняет путь.

Логика фильтрации:
- Событие сохранения должно завершиться успешно (`Status == Succeeded`).
- Семейства (.rfa) и пустые `PathName` игнорируются.
- Реагируем только на документ, который является текущим активным
  (чтобы не переключать базу при «Save All» фоновых документов).
- Если путь совпадает с уже известным, событие не поднимается
  (не реагируем на обычный `Ctrl+S`).

`FamilyManagerMainViewModel` слушает `ActiveDocumentPathChanged` и вызывает
`IProjectBaseActivator.ActivateForDocumentAsync` с новым путём.

## Update A3 — двусторонняя конвертация типов баз (2026-07-28, #168)

Первоначально тип базы задавался только при создании: `ConfigureProjectBaseAsync`
умел General→Project, но UI не давал вызвать его для общей базы, а обратного пути
Project→General не существовало. Добавлены две команды в popup «Инструменты базы»:

- **«Сделать проектной базой»** (General, Owner/BimMaster) — открывает
  `ProjectBaseRulesEditorView` с пустым шаблоном, затем вызывает существующий
  `ConfigureProjectBaseAsync`. Проектная база без шаблона бессмысленна (никогда
  не матчится), поэтому шаблон обязателен.
- **«Сделать общей базой»** (Project, Owner/BimMaster, Yes/No-подтверждение) —
  новый `IDatabaseManager.ConvertToGeneralBaseAsync`.

Видимость команд контекстная (`CanConvertSelectedToProject/General` +
`BoolToVis`): отображается только применимая к выбранному типу базы команда,
для роли Engineer обе скрыты — как у команды «Обновить базу» (ADR-054).

Решения:

- **Binding при Project→General очищается** (`registry.json: ProjectBinding = null`,
  `database_meta.project_binding_json = NULL`). Альтернатива «сохранить шаблон на
  будущее» отвергнута: общая база не должна хранить project-артефактов, а
  `ConnectDatabaseAsync` подхватывал бы мёртвый JSON в `ProjectBinding`.
- **Источник истины не меняется** — `database_meta` (Update A1): конвертация
  переживает disconnect/reconnect, реестр остаётся runtime-кешем.
- **RBAC — `CanEdit` (Owner/BimMaster)**: конвертация — write-операция уровня
  редактирования, как импорт/обновление базы. Не Owner-only (это не деструктивное
  удаление). Заодно закрыта дыра #169: `ConfigureProjectBaseCommand` получил
  `CanEdit`-гейт и `RefreshCurrentUserAsync()` после cross-DB записи
  (восстановление role-correct write access активной базы, I-14).
- **Миграции не нужны**: колонки `base_type`/`project_binding_json` существуют с
  v20/v21; конвертация — runtime UPDATE + `registry.json`, чистый JSON+SQLite без
  Revit API (как `SwitchDatabaseAsync`). Гейт `EnsureUpToDateAsync` не применяется —
  управление подключениями БД не гейтится (docs/architecture/database-migrations.md).
- Обе операции идемпотентны и работают для неактивной базы через существующий
  `UpdateTargetCachedBaseTypeAsync` (переключение пути с возвратом на активную).
- **Порядок записи — сначала `catalog.db` (источник истины), затем `registry.json`
  (runtime-кэш)**, как и в `CreateProjectDatabaseAsync`: при сбое UPDATE (SMB,
  SQLITE_BUSY) оба хранилища консистентно остаются в старом состоянии; при сбое
  записи реестра после успешного UPDATE состояние самовосстанавливается при
  reconnect — БД побеждает. `ConfigureProjectBaseAsync` приведён к этому же
  порядку (ранее писал реестр первым).
- `ConvertToGeneralBaseAsync` — самовосстанавливающийся: General-соединение с
  «осиротевшим» `ProjectBinding` (повреждённое состояние) нормализуется, а не
  считается no-op.
- **Сериализация реестра (#171)**: ручное тестирование этой фичи выявило, что
  fire-and-forget автоактивация (Update A2) гоняет сама с собой и с командами
  UI на `registry.json.tmp` — файл реестра повреждался и откатывался на `.bak`
  с потерей свежих изменений. Все async-мутаторы `DatabaseManager` сериализованы
  operation-level `SemaphoreSlim` (реентерабельность — через
  `CreateDatabaseCoreAsync`). Sync-читатели (`ListConnections`) lock-free
  намеренно (deadlock-risk на UI-потоке). Детерминированный стресс-тест:
  `ConcurrentRegistryMutations_RegistryStaysValidAndConsistent`.

## Verification

- Сборка R19/R21/R24/R25 — 0 warnings / 0 errors.
- Тесты: `ProjectBaseBindingEvaluatorTests`, `ProjectBaseActivatorTests`, все
  существующие `FileNameParserTests` проходят.
- Manual Revit test: создание/редактирование проектной базы + автоактивация при
  смене документа; первое сохранение нового проекта и SaveAs с переименованием.
