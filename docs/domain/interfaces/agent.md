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

## IDynamicToolRanker

Ранжирование не-core каталога под конкретный запрос для turn-start набора тулов
(decision-модель); при отключении/сбое вызывающий откатывается на BM25 по карточкам.

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