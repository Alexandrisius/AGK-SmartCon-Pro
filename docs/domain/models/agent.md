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

## AgentActionDigest

Дайджест действия агента для компакции истории.

## ElementCard

Карточка элемента модели для live-поиска (ADR-077).

## ViewCard

Карточка вида для live-поиска.

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