---
module: agent-interfaces
---
# Интерфейсы Agent (SmartCon.Core)

> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs` (Agent-контракты).
> WPF-free абстракции harness; реализации — SmartCon.Agent / SmartCon.Revit.
> Модели: [../models/agent.md](../models/agent.md).

## IAgentTool

Контракт исполняемого тула (имя, schema, Execute поверх `IRevitOps`, dryRun).

## IToolRegistry

Реестр тулов: регистрация/резолв определений, каталог, паки.

## IAgentToolPreflight

Предполётная проверка тула (валидация входа, risk, confirmation requirement).

## IAgentLlmClient

Клиент LLM (провайдер/модель, streaming, tool-calls). OpenRouter/OpenCode Go и др.

## IAgentAwaitableEvent

Маршалинг async-операций агента на Revit API-поток (ExternalEvent, RaiseAsync).

## IAgentConfirmationService

Гейт подтверждения write-операций (режимы, risk-предохранители, ledger).

## IAgentConversationStore

Персистентное хранение сессий/истории (`AgentConversation`, `AgentMessage`).

## IAgentWorkingStateStore

Хранение working state сессии между ходами.

## IAgentCompactionStore

Хранение записей компакции (checkpoint/restore).

## IAgentContextCompactor

Компакция истории контекста (digest при ~60% бюджета, ladder).

## IAgentSummarizer

Суммаризация истории/результатов для компакции и отчётов.

## IAgentChangeSetStore

Хранение change sets (affected-элементы, undo).

## IAgentToolResultBlobStore

Blob-store крупных результатов тулов (store-and-reference).

## IAgentAuditLog

Журнал аудита действий агента (`AgentAuditEntry`).

## IAgentTelemetryStores

Хранилища телеметрии (token_usage, tool_calls, eval, feedback-архив).

## IAgentProjectContextProvider

Поставщик контекста проекта Revit (`AgentProjectContext`).

## Job engine (ADR-079)

| Интерфейс | Назначение | Реализация |
|---|---|---|
| `IJobAction` | Одно массовое действие: `Expand` (заморозка юнитов), `Preview` (dry-run), `Apply` (в транзакции раннера), `Verify` (независимый read-back), `Undo` (per-item undo). `Scope` = ProjectInstance/ProjectType/FamilyDefinition/RfaFile | `RevitJobActionBase` + `RenameViewsJobAction`, `SetParametersJobAction`, `MapFamilyParametersJobAction` (SmartCon.Revit) |
| `IJobStore` | Durable-хранилище: CAS-переходы, юниты/батчи/события, курсор после `Document.Save()`, lease | `SqliteAgentJobStore` (SmartCon.Agent, SQLite v5) |
| `IJobScheduler` | `Start` (preview/apply/resume), `Cancel`, `EnsurePump` | `JobRunner` (SmartCon.Revit) |
| `IJobRecovery` | `Reconcile` осиротевших job'ов, `Rollback`, `RetryFailed` | `JobRunner` |
| `ITransactionScope` | Явный commit/rollback на батч (J-03), создаётся только `ITransactionService.BeginTransaction` | `RevitTransactionService` |
