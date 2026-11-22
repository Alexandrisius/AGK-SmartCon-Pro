---
module: agent
---
# Модели и сервисы Agent (SmartCon.Core)

> Источник истины: `src/SmartCon.Core/Models/Agent/*.cs`, `src/SmartCon.Core/Services/Agent/*.cs`.
> AI-agent harness: tool = намерение (ADR-076), живой поиск по модели (ADR-077),
> план + confirmation gate + change sets + undo (H1). WPF-free слой Core; UI/Revit —
> в SmartCon.Agent / SmartCon.Revit. Интерфейсы: [../interfaces/agent.md](../interfaces/agent.md).

## AgentConversation

Персистентная сессия агента (привязка к документу Revit): история сообщений, working state,
идентификатор сессии. Хранится через `IAgentConversationStore`.

## AgentMessage

Одно сообщение диалога (роль user/assistant/tool, текст, tool-calls, timestamp). Элемент
истории `AgentConversation`.

## AgentChatRequest

Запрос хода агента: сообщение пользователя + контекст документа + режим подтверждения.
Вход `IAgentLlmClient` / оркестратора хода.

## AgentTurnContextBuilder

Собирает контекст хода (системный промпт + история + working state + project context)
в запрос к LLM, применяя компакцию и бюджет токенов.

## AgentSystemPrompt

Формирует системный промпт агента (инварианты Revit, tool surface, правила безопасности).

## AgentPromptHash

Хэш системного промпта/контекста для дедупликации и телеметрии (стабильность промпта между ходами).

## AgentProjectContext

Снимок контекста проекта Revit для агента (документ, категории, уровни, активный вид).
Поставляется `IAgentProjectContextProvider`.

## AgentDecision

Решение routing-слоя (выбор тула/навыка, confidence, clarify-опция). JEV/rules/hybrid routing.

## AgentToolDefinition

Описание тула: имя, schema параметров, pack, purpose, examples, synonyms. Каталог `AgentToolCatalog`.

## AgentToolCard

Карточка тула для retrieval (Pack/Purpose/Examples/Synonyms) — BM25-поиск по каталогу.

## AgentToolCards

Статический реестр карточек тулов (always-core + паки). Источник каталога/shortlist.

## AgentToolCards.Model

Карточки тулов живого поиска по модели (ADR-077): `list_categories`, `list_levels`,
`list_family_types`, `list_parameters`, `find_families_with_parameter`, `query_elements`,
`summarize_parameter_values`.

## AgentToolCards.Views

Карточки тулов видов: `list_views`, `get_view_settings`, `rename_views`, `apply_view_template`.

## AgentToolCards.ViewSettings

Карточки тулов настройки вида: `set_view_range`, `set_view_visibility`, `set_view_crop`,
`copy_view_settings`, `configure_views`.

## AgentToolCards.Sheets

Карточки тулов листов.

## AgentToolCards.Schedules

Карточки тулов спецификаций.

## AgentToolCards.Filters

Карточки тулов фильтров видов и graphic overrides.

## AgentToolCards.Families

Карточки тулов семейств (H1d): `list_families`, `get_family_parameters`,
`add_family_parameter`, `map_family_parameters`, `save_family`.

## AgentToolCards.Write

Карточки write-тулов (dry-run по умолчанию, confirmation gate).

## AgentToolCards.Admin

Карточки admin-тулов (P0-стресс-тест): `list_filters`, `get_filter_details`,
`set_filter_rules`, `delete_filter`, `delete_views`, `delete_sheets`,
`delete_family_types`, `remove_family_parameter`, `set_view_parameters`,
`rename_filter`, `get_schedule_definition`, `list_shared_parameters`.

## AgentToolDisplayNames

Локализованные человекочитаемые названия тулов (tool name → «Список видов»)
для компактных заголовков tool-call в UI.

## AgentToolCatalog

Каталог тулов: реестр определений, BM25-retrieval shortlist, паки, recency (`TouchTool`).

## AgentToolPacks

Группировка тулов в паки (read/write/view/family и т.д.); `load_tool_pack` расширяет каталог.

## AgentToolPackSkills

Короткий markdown-навык пака, который `load_tool_pack` возвращает вместе с паком (#400): словарь аргументов в терминах Revit UI, инварианты записи и рецепты, проверенные на реальных моделях. Сейчас навык есть у пака `filters`.

## AgentConversationTitle

Политика заголовка сессии: первый вопрос пользователя становится заголовком. `MaxLength = 200` — потолок для широкой панели; фактическую ширину UI обрезает сам, а не фиксированные 60 символов. `FromFirstMessage` схлопывает `\r`/`\n` в пробелы, тримит и добавляет «…» при превышении лимита.

## AgentToolContext

Контекст выполнения тула (документ, change set, режим подтверждения, OpId).

## AgentToolInputRepair

Нормализация/починка входа тула перед исполнением (алиасы параметров, локализованные ключи).

## AgentToolSchemaValidator

Валидация вызова тула против JSON-схемы параметров (schema-valid calls метрика eval).

## ToolResult

Результат выполнения тула (данные, truncated/hint, matchedParameter/matchedValue).

## ToolResultBudget

Структурная обрезка массивов результата под бюджет контекста (валидный JSON +
returned/truncated/hint); broad-query guard `query_too_broad`.

## AgentToolResultBlob

Ссылка на крупный результат тула, вынесенный в blob-store (store-and-reference, H1-14).

## QuerySpec

Структурный спецификатор запроса элементов (categories/levelIds/conditions/parameterContainsAny).

## RuleToolRouter

Rules-routing тулов по ключевым словам/намерению (fallback и hybrid-режим).

## ToolCardRouter

Retrieval-shortlist карточек тулов (BM25) по тексту запроса; двухуровневый routing с `find_tools`.

## AgentPlan

План действий агента (секции/шаги, planDigest, dryRun). Исполняется `AgentPlanExecutor`.

## AgentPlanExecutor

Исполнитель плана поверх `IRevitOps`: apply секций, change set, undo, per-section отчёт,
partial-failure контракт. planDigest привязан к документу.

## AgentPlanExecutor.Steps

Исполнение отдельных шагов плана ( partial-файлы `AgentPlanExecutor.Steps.cs`).

## AgentPlanExecutor.Audit

Аудит исполнения плана (записи `AgentAuditEntry`, apply-отчёт из `ApplyResult`).

## AgentPlanExecutor.Conflicts

Обработка конфликтов шагов плана (пересечения affected-элементов).

## AgentPlanExecutor.Resolution

Разрешение конфликтов/ожиданий плана (expectationsChecked, honest hint).

## AgentPlanValidator

Валидация плана (уровни L0–L6): структура, ссылки на элементы, риск, dry-run.

## ChangeSet

Набор изменений плана/тула (affected ElementId, before/after), основа undo и confirmation gate.

## AgentConfirmation

Запрос подтверждения write-операции (режимы ConfirmWrites/AutoApproveSession/ReadOnly).

## AgentConfirmationLedger

Журнал подтверждений (что подтверждено/отклонено, risk-гейты Destructive/workshared/>200).

## AgentApprovalMode

Режим подтверждения writes (enum: ConfirmWrites / AutoApproveSession / ReadOnly).

## AgentWorkingStateUpdater

Обновляет working state агента после хода (компакция, store).

## WorkingState

Рабочее состояние сессии (текущий контекст/фокус, персистентно между ходами).

## AgentCompactionRequest

Запрос компакции истории (порог ~60% бюджета контекста).

## AgentCompactionResult

Результат компакции (digest, сохранённые/обрезанные сообщения).

## AgentCompactionRecord

Персистентная запись компакции (compaction ladder + 9-section checkpoint + restore, H1-15/16/17).

## AgentRedactor

JSON-aware редакция чувствительных данных в логах/телеметрии (ключи любого типа).

## AgentTextSanitizer

Очистка текста (markdown/ответы LLM) перед рендером/хранением.

## AgentTextAudit

Аудит текста агента (проверка на утечки/запрещённые конструкции).

## AgentAuditEntry

Запись аудита действия агента (tool/plan, OpId, результат, timestamp).

## AgentTelemetry

Телеметрия хода (token_usage, latency, tool selection accuracy, skills).

## AgentTokenCalibration

Калибровка подсчёта токенов промпта/completion (token counter UI).

## LoopGuard

Предохранитель циклов агента (3 block / 5 abort) — единственное ограничение хода besides Stop.

## AgentSchema

JSON-схемы тулов/параметров (источник для `AgentToolSchemaValidator`).

## AgentFamilyText

Текстовые хелперы семейств для агента (нормализация имён, поиск по описанию).

## AgentFamilyUnits

Единицы/конверсия значений параметров семейств для агента.

## AgentListPayload

Общие части payload list-тулов: проекция записи на запрошенные `fields` 
(неизвестные имена → текст ошибки со списком допустимых), поля пейджинга 
`total/returned/offset/nextOffset/truncated` и подсказки модели — как сузить 
обрезанный список и как выбрать другие поля. Чистый C#, без Revit API.

## AgentActionDigest

Дайджест действия агента для компакции истории.

## ElementCard

Карточка элемента модели для live-поиска (ADR-077).

## ViewCard

Карточка вида для live-поиска.

## ViewSettings / ViewSettingsSummary / ViewTemplateCard

`Models/Agent/ViewCard.cs` — снимок настроек вида и шаблона.

- `HiddenCategoryCount` — все скрытые категории вида (ровно то, что показывает
  `get_view_settings`); `HiddenCategoryEditableCount` — сколько из них Revit ещё разрешает
  переключать на этом виде (`CanCategoryBeHidden`). Остальные скрыты, но здесь не
  редактируются: V/G может управляться шаблоном, либо категорию нельзя скрывать в этом
  типе вида. Счётчик не приписывает причину. Writer считает по тому же правилу, поэтому
  diff записи сходится с чтением.
- `ViewTemplateCard.Kind = "template"` помечает ответ `get_view_template_details`.
  Вложенный `settings` описывает значения самого шаблона: `hasTemplate=false` и
  отсутствующий `templateId` там ожидаемы — шаблон не может использовать другой шаблон.

## ElementSummaryGroup

Группа сводки элементов (`summarize_parameter_values`, topN/excludeInternal).

## AgentEvalTask

Одна eval-задача (фикстура replay/live, expected tool/data).

## AgentEvalTasks

Набор eval-задач (40+ фикстур, case coverage).

## AgentEvalMetrics

Метрики eval (read/write/apply+undo success, routing accuracy, schema-valid, пороги гейта).

## LocalizationService.Keys.Agent

Ключи локализации Agent-модуля (вложенный класс `LocalizationService.Keys`).

## JobModels

Каталог моделей job engine (ADR-079): `src/SmartCon.Core/Models/Agent/Jobs/JobModels.cs`.
Файл группирует enum'ы и DTO одного контракта; ниже — ключевые типы и изменения
раунда приёмки массовых операций.

**Файл:** `Models/Agent/Jobs/JobModels.cs`

**JobMode / JobState / JobItemState.**

`JobMode` — `Preview`/`Apply`/`Resume`. `JobState` — жизненный цикл задания:
`PreviewRunning → PreviewReady → Running → Completed/CompletedWithFailures/Failed/Cancelled/Interrupted/RolledBack/RollbackPartial`
(+ `Cancelling`, `AwaitingApproval`). `JobItemState` — судьба юнита:
`Pending → Previewed → Applying → Done/Skipped/Failed/Mismatch`, откат:
`Undoing → Undone/UndoFailed`.

**JobOptions / JobOptionsOverride / JobOptionsMerger.**

`JobOptions` — лимиты и политики задания. В раунде приёмки добавлены:

- `LargeJobThreshold = 200` — порог, выше которого apply/resume требует
  явного `AllowLargeJob`;
- `AllowLargeJob` — подтверждение владельца, что большое задание действительно
  нужно применить. Preview это значение **не сохраняет**, чтобы его нельзя было
  пронести в apply без отдельного подтверждения.

`JobOptionsOverride` — nullable-политики, переданные только в apply/resume.
`JobOptionsMerger.Merge(stored, overrides)` накладывает их поверх preview-опций,
не сбрасывая остальные значения на дефолты.

```csharp
public sealed class JobOptions
{
    public const int LargeJobThreshold = 200;
    public int BatchSize { get; init; } = 50;
    public int SliceBudgetMs { get; init; } = 1500;
    public JobOnError OnError { get; init; } = JobOnError.Continue;
    public bool RollbackOnError { get; init; }
    public JobSavePolicy SavePolicy { get; init; } = JobSavePolicy.AfterEachBatch;
    public int SaveEveryMinutes { get; init; } = 5;
    public int MaxItems { get; init; } = 1000;
    public bool AllowLargeJob { get; init; }
}

public sealed class JobOptionsOverride
{
    public JobOnError? OnError { get; init; }
    public bool? RollbackOnError { get; init; }
    public JobSavePolicy? SavePolicy { get; init; }
    public int? BatchSize { get; init; }
    public int? MaxItems { get; init; }
    public bool? AllowLargeJob { get; init; }
}
```

**JobSpec.**

Сериализуемое описание задания: `schemaVersion`, `documentId`, `action`, `mode`,
`specJson`, `options`, `optionsOverride`, `previewDigest`, `resumeJobId`,
`conversationId`. `OptionsOverride` появился, чтобы apply/resume могли включить
`onError=abort` или `rollbackOnError`, не теряя остальные preview-политики.

**JobUnit / JobExpansion.**

`JobUnit` — замороженный юнит (`seq`, `unitKey` — idempotency key, `argsJson`,
`label`). `JobExpansion` — результат `Expand`: юниты + warnings + typed error.

**JobUnitPreview / JobUnitApply / JobUnitVerification / JobUnitUndo.**

Dry-run, факт применения, независимый read-back и откат одного юнита. `JobUnitApply`
несёт `success/skipped/expectedJson/undoJson` и typed error; `JobUnitVerification`
— единственный источник «done».

**JobActionContext.**

Контекст вызова action на Revit-потоке: `Document`, `DocumentId`, `JobId`,
`DryRun`, `SpecJson`, `CancellationToken`.

## JobSpecKeyValidator

Строгая проверка ключей action-spec **до** любой Revit-работы. System.Text.Json
молча игнорирует неизвестные поля, поэтому action объявляет allow-list путей
(`select`, `rename`, `rename.numbering`, `mappings[]`, `mappings[].source`,
`select.conditions[]`). Неизвестный ключ возвращает `job_spec_invalid` со списком
допустимых ключей.

**Файл:** `Models/Agent/Jobs/JobSpecKeyValidator.cs`

```csharp
public static class JobSpecKeyValidator
{
    public static string? Validate(string? specJson, params JobSpecObjectRule[] rules);
}

public sealed class JobSpecObjectRule
{
    public required string Path { get; init; }
    public required IReadOnlyList<string> AllowedKeys { get; init; }
}
```

## JobSpecValidator

Валидация `JobSpec` (schemaVersion, documentId, action, режим, обязательный digest
для apply, resumeJobId для resume, диапазоны options и options-override) и
apply-envelope: digest, документ, action, expiry. Не заменяет action-specific
`JobSpecKeyValidator`.

**Файл:** `Models/Agent/Jobs/JobSpecValidator.cs`

## JobPreviewDigest

Канонический SHA256 превью: action + documentId + selector hash + spec hash +
count + willChangeCount + expiry. `JobSavePolicyDecider` (тот же файл) содержит
чистые решения `after_each_batch`/`every_n_minutes`/`never`.

**Файл:** `Models/Agent/Jobs/JobPreviewDigest.cs`

## JobPreview

Frozen preview: `count`, `willChangeCount`, `skipCount`, `failedCount`, `sample`,
`selectorHash`, `specHash`, `digest`, `expiresAtUtc`. `JobPreviewSample` теперь
несёт `status`, `errorCode`, `errorDetail`: причина падения видна уже в preview,
а не только в отчёте.

**Файл:** `Models/Agent/Jobs/JobPreview.cs`

```csharp
public sealed class JobPreviewSample
{
    public string? UnitKey { get; init; }
    public string? Label { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public bool WillChange { get; init; }
    public string? Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorDetail { get; init; }
}
```

## JobRecord

Строка `jobs`: spec, state, `cancelRequested`, `previewJson`, durable cursor,
totals, lease, timestamps. В том же файле — `JobItem`/`JobItemUpdate` (строки
`job_items` и мутабельная проекция), `JobTotals` (`Incomplete` =
Pending+Applying+Undoing+Undone запрещает `Completed`; `Stale` считает
`job_preview_stale`, `AppliedBefore` — применённые до отката юниты), `JobEvent`
и `JobEventTypes` (append-only события, включая `preview_superseded`).

**Файл:** `Models/Agent/Jobs/JobRecord.cs`

## JobReport

Операторский отчёт, построенный независимым read-back'ом. Помимо
`truthful/mismatchCount/пагинации` содержит:

- `ReadBack` — выполнялся ли read-back реально (а не был запрошен и пропущен);
- `Cancelled`/`InterruptedUnits` — отмена видна отдельно от обычного отката;
- `Errors` — агрегация `JobErrorGroup` по `errorCode` (`count`, `exampleDetail`,
  `firstSeq/lastSeq`), чтобы 500 ошибок не разбирать вручную.

`JobRollbackResult` и `JobRetryResult` описывают результаты `rollback_job` и
`retry_failed_items`; у retry появились `Attempted`, `StillFailed` и `Totals`.
`JobReportItem` — строка отчёта (`expected/actual/undo/verified/attempts`).

**Файл:** `Models/Agent/Jobs/JobReport.cs`

## JobErrors

`JobError` — typed error + actionable hint (`[Action: …]`). `JobErrorCodes` —
единый набор кодов; в раунде приёмки добавлены `job_preview_superseded`,
`job_confirmation_required`, `job_aborted_by_policy`.

**Файл:** `Models/Agent/Jobs/JobErrors.cs`

## JobStateMachine

Таблица допустимых переходов + `Validate` (CAS в store сверяет `from`).
`NeedsPump` определяет, нужен ли ещё slice; `IsTerminal` — финальные состояния.

**Файл:** `Models/Agent/Jobs/JobStateMachine.cs`

## JobJson

Единые JSON-опции job-контракта: camelCase, case-insensitive, без отступов.
Один serializer для Core, SQLite-store и Revit-runner.

**Файл:** `Models/Agent/Jobs/JobJson.cs`

## JobStartResult

Результат `IJobScheduler.Start`: либо `Job`, либо typed `Error`; `Success`
требует оба условия.

**Файл:** `Models/Agent/Jobs/JobStartResult.cs`

## AgentToolCards.Jobs

Карточки job-инструментов (`start_job`, `get_job_status`, `cancel_job`,
`get_job_report`, `rollback_job`, `retry_failed_items`) для routing-каталога:
назначение, когда использовать, примеры, синонимы. `cancel_job` помечен
идемпотентным; `retry_failed_items` описывает пере-проверку failed-юнитов.

**Файл:** `Services/Agent/AgentToolCards.Jobs.cs` (partial `AgentToolCards`).
