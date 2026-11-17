# Handoff — 2026-11-18 (Phase A–E checkpoint: write layer, confirmation gate, context budget)

## Состояние

Ветка `develop`. Коммиты этой сессии:

| Коммит | Содержание |
|---|---|
| `d79ee3d7` | feat(agent): write layer — 10 write-tools, confirmation gate, change sets, undo_last, integration/unit tests |
| (следующий) | feat(agent): context budget — ToolResultBudget, лимиты, broad-query guard, проекции, компакция истории |

### Что работает

- **Read-слой (P1)**: 12 инструментов (`get_project_overview`, `list_views`, `list_levels`,
  `list_categories`, `get_selected_elements`, `query_elements`, `get_element_details`,
  `summarize_elements`, `list_view_templates`, `get_view_settings`, `get_view_template_details`,
  `get_current_view_info`).
- **Write-слой (P2)** — `SmartCon.Revit/Agent/Tools/Write/`:
  `rename_views`, `rename_levels`, `apply_view_template`, `set_view_range`, `set_view_crop`,
  `set_view_visibility`, `configure_views`, `copy_view_settings`, `set_parameters`,
  `remap_parameter_values`, `undo_last`.
  Все: `dryRun=true` по умолчанию, отказ при `doc.IsWorkshared` (`workshared_not_supported`),
  лимиты на батч, отчёт `{dryRun, changed[], skipped[], failed[], total, affected, changeSetId?}`,
  change items в `agent.db` для undo.
- **Confirmation gate**: `AgentToolExecutor` требует подтверждения для любого write c
  `dryRun=false`; digest SHA-256 канонического JSON (`AgentActionDigest`); WPF-оверлей
  в `AgentPaneControl.xaml` (`IsConfirmationVisible`, команды Approve/Reject);
  событие `AgentConfirmationService.Requested`; audit-запись `<tool>#confirm`.
- **Context budget (P3)**:
  - `SmartCon.Core/Models/Agent/ToolResultBudget.cs` — структурная обрезка массивов
    (`elements/views/levels/groups/parameters/cards/changed/skipped/failed/items/templates`),
    добавляет `<name>Returned`, `<name>Total`, `truncated`, `hint`; JSON всегда валиден;
    fallback — digest-envelope. Никакого `Substring`.
  - `list_views`: default 50 / max 500, `summaryOnly`, `fields[]`.
  - `query_elements`: default 50 / max 200 (`QuerySpec.DefaultLimit/MaxLimit`), `offset`/`nextOffset`,
    `fields[]`, guard `query_too_broad` (без фильтров и `limit > 50`).
  - `get_element_details`: max 10, `includeTypeParameters=false` по умолчанию,
    `parameterNames[]`, `onlyNonEmpty`.
  - `AgentHistoryCompactor` (SmartCon.Agent): при оценке > 60% от
    `AgentLlmSettings.ContextWindowTokens` (default 200 000) старые tool-сообщения заменяются
    digest-ом `{compacted, success, message, affectedCount, truncated, hint}`; последние 4
    tool-результата сохраняются; факт компакции логируется.
  - System prompt: блоки «Write workflow» и «Context budget».

## Проверено (в этой сессии)

- R25 build: 0/0 (App, Revit, IntegrationTests).
- Unit Agent: **66/66** (`dotnet test ... --filter "FullyQualifiedName~Agent"`).
- Integration Agent R25: read 14/14 (baseline), write **24/25** + 1 skip
  (`SetViewRange_OnThreeDView` — в дефолтном шаблоне нет 3D-вида), budget **4/4**.
- Multi-version baseline sweep: 7 конфигураций 0/0 (`tools/build-all-configs.ps1`).
- Полный прогон всего integration-сьюта R25 после write-слоя **ещё не делался** (см. §Долг).

## Ключевые факты Revit API (проверено через Exa + интеграционные тесты)

1. `View.CropBox` setter **игнорирует Transform** — только Min/Max. Для копирования crop
   между видами используем `GetCropRegionShapeManager().GetCropShape()/SetCropShape()`
   (координаты в системе вида) + перенос Min/Max. Перед `SetCropShape` обязательно
   `CropBoxActive = true`, иначе исключение.
2. `View.ViewTemplateId` — setter; `ArgumentException`, если шаблон не подходит виду
   (другое `ViewFamily`) или вид сам шаблон/лист/ведомость. Проверяем `ViewFamily` через
   `ViewFamilyType` и ловим исключение; `overwriteTemplate=false` по умолчанию.
3. `Level.Name` каскадно переименовывает виды с точно таким же именем. Поэтому
   `rename_levels` проверяет `cascade_conflict` (новое имя не должно совпадать с именем
   другого вида).
4. `Element.Name`/`View.Name`/`Level.Name` бросают `ArgumentException` при дубле;
   циклические переименования внутри батча решаются временными именами
   (`AgentRenamePlanner`).
5. `Parameter.Set` — перегрузки Double/Int32/String/ElementId; `StorageType` обязателен
   для выбора. Значения Double в change items храним как **raw internal units** (`"R"`),
   иначе undo восстанавливает мм как футы. `Parameter.IsReadOnly` покрывает formula-параметры
   (свойства `IsDeterminedByFormula` в API нет).
6. `Category.GetCategory(doc, id)` — единственный способ найти категорию по ElementId
   (`Categories.get_Item` принимает только string/BuiltInCategory).

## Грабли, на которые уже наступили

- `ExecuteForTests` в `AgentWriteToolBase` берёт `documentId` из `IRevitContext`, иначе
  `undo_last` не находит change set (он ищет по document id).
- Приватный `Apply` в публичном базовом классе не может принимать `internal` тип —
  `WriteOutcome/WriteBatch/WriteItemReport` сделаны `public`.
- `IReadOnlySet<T>` недоступен на net48 — в `GetElementDetailsTool` используется `HashSet<T>`.
- Raw-интерполяция `$$"""..."""` ломается на JSON с `}}` — в тестах собираем строки
  конкатенацией.
- `ElementId` в тестах — только через `ElementIdCompat.Create/GetValue()` (R23 32-bit).
- Тестовый хост без UI: write-tools вызываются через `ExecuteForTests(args, approved, ct)`,
  `ITransactionService` — на `StubRevitContext`, change set store — `InMemoryAgentChangeSetStore`
  (SQLite-стор живет в SmartCon.Agent, а его нельзя тянуть в IntegrationTests — WPF/RevitAPIUI).

## Долг / что осталось

1. **Фаза F — dogfooding** (не начата):
   - meta-tool `report_tooling_feedback` (Risk=Read) → таблица `agent_feedback` в `agent.db`
     (схема: id, session_id, document_id, model, provider, tool_name, area, rating, issue,
     suggestion, context_json, created_at) + `smartcon.log`. Нужен bump `AgentDatabase.SchemaVersion`
     до 2 с `CREATE TABLE IF NOT EXISTS agent_feedback`.
   - заполнение `tool_calls` (сейчас пустая): `IAgentToolCallStore` + вызов из
     `AgentOrchestrator` рядом с audit (поля: session_id, tool_name, arguments_json,
     result_json, status, error, duration_ms, created_at).
   - `AgentFrictionAnalyzer` — отчёт по `agent.db` (частоты invalid_arguments/tool_failed/
     document_changed/truncated, средние раунды, топ-проблемные тулы).
   - `AgentLiveDogfoodTests` (IntegrationTests, read-only, skip без `SMARTCON_AGENT_LIVE=1`
     и ключа). ВАЖНО: не ссылаться на SmartCon.Agent (RevitAPIUI риск) — реализовать
     локальный минимальный OpenAI-compatible клиент в тесте; 10–12 русских задач;
     транскрипт в `%TEMP%\smartcon-agent-dogfood\<timestamp>.md`; max 30 turns.
   - `docs/agent/AGENT-FEEDBACK.md` + топ-3–5 улучшений инструментов по итогам.
2. **Фаза G**: token_usage persistence, настройка порога компакции, plan validation L0–L6.
3. **Фаза H**: полный R25 integration-сьют, net48-прогон (`-p:RevitVersion=2023`),
   R27-прогон (если Revit 2027 установлен — на машине есть), sweep 7 конфигураций,
   HANDOFF, коммиты, push.
4. Ручная приёмка записи в Revit (владелец, утро): dry-run → Apply → проверка модели →
   `undo_last`.
5. Ручная приёмка UI-оверлея подтверждения (кнопки Apply/Cancel, digest, таймаут).

## Команды

```bash
# сборка
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
powershell -NoProfile -ExecutionPolicy Bypass -File "tools/build-all-configs.ps1"

# unit (Agent)
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~Agent"

# integration (Agent), класс в 3-м сегменте фильтра
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*Agent*/*"
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentWrite*/*"
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentContextBudget*/*"

# net48 (Revit 2023 установлен)
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023 -- --treenode-filter "/*/*/*Agent*/*"
```

## Ограничения

- Ветка `develop`; push в develop разрешён, в `main` — нет, PR не создавать.
- `build-and-deploy.bat` НЕ запускать (Revit может быть открыт) — деплой утром у владельца.
- Не коммитить `opencode.json` и `server/` (чужая работа), `secrets.env` вне репозитория.
- Ключи не логировать; Exa — только обезличенные технические запросы.
- Write-tools не пишут в workshared central — отказ `workshared_not_supported`.
- `docs/` вне `docs/agent/`, ADR и changelog не трогать до ручной приёмки.
