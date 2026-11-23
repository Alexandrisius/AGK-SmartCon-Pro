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

## IAgentFilterQuarantineStore

Карантин удалённых view-фильтров (S.26): полный снимок каждого фильтра
(правила, категории, применённые виды с overrides — `AgentFilterSnapshot`)
персистится в agent.db до удаления, потому что удалённый фильтр не
восстановить `undo_last`. `SaveAsync`/`GetAsync`; `CleanupAsync` удаляет
снимки старше cutoff и возвращает число удалённых строк (чистка на записи —
после 90 дней); восстановление — ручная задача по сохранённому JSON.
Реализация — `SqliteAgentFilterQuarantineStore` (SmartCon.Agent); пишут
`DeleteFilterTool`/`MergeDuplicateFiltersTool` через `AgentFilterQuarantine`
(SmartCon.Revit).

**Файл:** `Services/Interfaces/Agent/IAgentFilterQuarantineStore.cs`

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

## IJobAction

Одно массовое действие job engine: `Expand` замораживает юниты, `Preview`
делает dry-run без изменения модели, `Apply` выполняется в транзакции раннера,
`Verify` — независимый read-back (единственный источник «done»), `Undo`
восстанавливает один юнит по persisted undo payload. Реализации —
`RevitJobActionBase` + `RenameViewsJobAction`, `SetParametersJobAction`,
`MapFamilyParametersJobAction` (SmartCon.Revit).

**Файл:** `Services/Interfaces/Agent/IJobAction.cs`

```csharp
public interface IJobAction
{
    string Name { get; }
    JobActionScope Scope { get; }
    int MaxUnits { get; }
    JobExpansion Expand(JobActionContext context, string specJson);
    JobUnitPreview Preview(JobActionContext context, JobUnit unit);
    JobUnitApply Apply(JobActionContext context, JobUnit unit, JobUnitPreview preview,
        int attempt, Action<string>? persistUndo);
    JobUnitVerification Verify(JobActionContext context, JobUnit unit, JobUnitPreview preview);
    JobUnitUndo Undo(JobActionContext context, JobUnit unit, string? undoJson, bool dryRun);
}
```

**IJobScheduler.**

`Start` (preview/apply/resume, первый slice может выполниться inline), `Cancel`
(останавливает между юнитами) и `EnsurePump` (поднять pump после рестарта).
Реализация — `JobRunner` (SmartCon.Revit).

**IJobRecovery.**

`Reconcile` осиротевших job'ов, `Rollback` (per-item undo, dry-run первым) и
`RetryFailed` (свежий preview только для failed-юнитов). Реализация — `JobRunner`.

## IJobStore

Durable-хранилище job'ов: CAS-переходы состояний, frozen preview, юниты, батчи,
append-only события, durable cursor после успешного `Document.Save()`, lease.
Реализация — `SqliteAgentJobStore` (SmartCon.Agent, SQLite v5; single writer,
DELETE journal, без WAL — I-14).

В раунде приёмки добавлен `UpdateSpecAsync`: apply/resume сохраняют
`Mode` и слитые `JobOptions` **после** успешного перехода состояния, не трогая
frozen preview и строки юнитов.

**Файл:** `Services/Interfaces/Agent/IJobStore.cs`

```csharp
public interface IJobStore
{
    Task<JobRecord> CreateAsync(JobSpec spec, CancellationToken ct);
    Task<JobRecord?> GetAsync(Guid jobId, CancellationToken ct);
    Task<JobRecord?> GetActiveAsync(string documentId, CancellationToken ct);
    Task<JobRecord?> GetByPreviewDigestAsync(string documentId, string digest, CancellationToken ct);
    Task<IReadOnlyList<JobRecord>> ListRecentAsync(string documentId, int limit, CancellationToken ct);
    Task<JobError?> TransitionAsync(Guid jobId, JobState expectedFrom, JobState to,
        string? errorCode, string? errorDetail, CancellationToken ct);
    Task UpdateSpecAsync(Guid jobId, JobSpec spec, CancellationToken ct);
    Task UpdatePreviewAsync(Guid jobId, string previewJson, CancellationToken ct);
    Task UpdateTotalsAsync(Guid jobId, string totalsJson, CancellationToken ct);
    Task UpdateCursorAsync(Guid jobId, int lastSavedCursor, DateTime savedAtUtc, CancellationToken ct);
    Task SetCancelRequestedAsync(Guid jobId, bool requested, CancellationToken ct);
    Task RenewLeaseAsync(Guid jobId, DateTime leaseUntilUtc, CancellationToken ct);
    Task MarkStartedAsync(Guid jobId, DateTime startedAtUtc, CancellationToken ct);
    Task MarkFinishedAsync(Guid jobId, JobState finalState, string? errorCode,
        string? errorDetail, CancellationToken ct);
    Task AddUnitsAsync(Guid jobId, IReadOnlyList<JobUnit> units, CancellationToken ct);
    Task<IReadOnlyList<JobItem>> LoadItemsAsync(Guid jobId, IReadOnlyList<JobItemState> states,
        int afterSeq, int limit, CancellationToken ct);
    Task<IReadOnlyList<JobItem>> LoadAllItemsAsync(Guid jobId, CancellationToken ct);
    Task<int> CountItemsAsync(Guid jobId, CancellationToken ct);
    Task UpdateItemsAsync(Guid jobId, IReadOnlyList<JobItemUpdate> updates, CancellationToken ct);
    Task ResetItemsAsync(Guid jobId, IReadOnlyList<int> seqs, JobItemState to, CancellationToken ct);
    Task<int> BeginBatchAsync(Guid jobId, int fromSeq, int toSeq, CancellationToken ct);
    Task CompleteBatchAsync(Guid jobId, int batchIndex, int applied, int failed,
        bool saved, DateTime? savedAtUtc, CancellationToken ct);
    Task AppendEventAsync(Guid jobId, string type, string? payloadJson, CancellationToken ct);
    Task<IReadOnlyList<JobEvent>> LoadEventsAsync(Guid jobId, int limit, CancellationToken ct);
}
```

**ITransactionScope.**

Явный commit/rollback на батч (J-03); создаётся только
`ITransactionService.BeginTransaction`, не напрямую.
