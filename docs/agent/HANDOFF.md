# Handoff — 2026-11-18 (Phase A–F: write layer, confirmation gate, context budget, dogfooding)

## Состояние

Ветка `develop`. Коммиты этой сессии:

| Коммит | Содержание |
|---|---|
| `d79ee3d7` | feat(agent): write layer — 10 write-tools, confirmation gate, change sets, undo_last, integration/unit tests |
| `b1d30ccb` | feat(agent): context budget — ToolResultBudget, лимиты, broad-query guard, проекции, компакция истории |
| (следующий) | feat(agent): dogfooding — report_tooling_feedback, tool_calls/token_usage, AgentFrictionAnalyzer, live dogfood, улучшения по фидбэку |

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
  - System prompt: блоки «Write workflow», «Context budget», «Tooling feedback».

### Dogfooding / телеметрия (P4)

- **`report_tooling_feedback`** — meta-tool (`SmartCon.Agent/Services/Tools/ReportToolingFeedbackTool.cs`,
  Risk=Read): area(tools|prompt|ui), toolName, rating 1–5, issue, suggestion, example →
  таблица `agent_feedback` (schema v2) + `smartcon.log`. Модель вызывает его сама
  (инструкция в system prompt, «один раз на проблему»).
- **`tool_calls`** заполняется оркестратором: id, session_id, tool_name, arguments_json,
  result_json (budget-trimmed), status ok/error, error code, duration_ms.
- **`token_usage`** заполняется по каждому ходу: provider, model, prompt/completion/
  reasoning tokens, cost_usd.
- **`AgentFrictionAnalyzer`** (`SmartCon.Agent/Services/Analysis/`): SQL-отчёт по
  `tool_calls`/`agent_feedback` за N дней + markdown (`ToMarkdown()`): ошибки по кодам,
  truncated, средние длительности, топ проблемных тулов, рекомендации.
- **`AgentLiveDogfoodTests`** (IntegrationTests, read-only, skip без `SMARTCON_AGENT_LIVE=1`):
  реальный OpenAI-compatible клиент (`DogfoodClient`, без ссылки на SmartCon.Agent),
  10–11 русских задач, бюджет 30 LLM-запросов, транскрипт в `%TEMP%\smartcon-agent-dogfood`.
- **Итоги прогона 1** (11 задач, 10 ответов, 3 feedback): исправлены 3 замечания —
  алиасы проекций + `ignoredFields`, округление mm до 0.1, дедупликация параметров и
  единицы в displayValue. Подробно — `docs/agent/AGENT-FEEDBACK.md`.

## Проверено (в этой сессии)

- R25 build: 0/0 (App, Revit, IntegrationTests).
- Unit Agent: **76/76** (`dotnet test ... --filter "FullyQualifiedName~Agent"`).
- Integration Agent R25 (фильтр `/*/*/*Agent*/*`): **45/47** успешно, 2 skip
  (`LiveDogfood_ReadOnlyTooling` без env-флага; `SetViewRange_OnThreeDView` — нет 3D-вида
  в дефолтном шаблоне).
- Live dogfood: прогон 1 — 11 задач, 10 ответов, 3 feedback, 29/30 запросов;
  прогон 2 — 11/11, 1 feedback, 27/30; **прогон 3 — 11/11, 0 feedback, 24/30**
  (все замечания закрыты, см. `AGENT-FEEDBACK.md`).
- Multi-version sweep: **7 конфигураций 0/0** (`tools/build-all-configs.ps1`; R23/R24
  потребовали один фикс net48-nullable в `AgentViewWriteHelper.Deserialize`).
- Полный unit-сьют: **3360/3360**.
- Полный integration-сьют R25: **293/305** успешно, 12 пропущено (контент-зависимые
  скипы nut-файлов и 3D-вида), 0 сбоев, 5m39s.
- net48 (Revit 2023, без Agent-тестов): **245/257** успешно, 12 пропущено, 0 сбоев, 5m15s.
- R27 (Revit 2027, net10, Agent-фильтр): **45/47** успешно, 2 пропущено, 1m02s.

### Ограничение net48 для Agent (важно)

Agent-интеграционные тесты **не компилируются для net48** и исключены из net48-сборки
`SmartCon.IntegrationTests` (`<Compile Remove="Agent\**\*.cs" />` при
`'$(TargetFramework)' == 'net48'`). Причина (pre-existing, не из этой сессии):
на net48 `SmartCon.Dependencies` несёт ILRepack-merged `System.Text.Json` 8.0.6
(ADR-051), а тест-хосту нужен реальный 9.0.0 для TUnit. `JsonElement` из этих двух
сборок — разные типы: любая передача `JsonElement` через границу (а весь Agent API
принимает `JsonElement args`) даёт CS0433/CS1503. Плюс `AgentReadToolsTests` использует
`new ElementId(long)`, которого нет в R23 (есть только в R24+).

Что это значит:
- Agent на net48 **компилируется** (App R23/R24 в sweep — 0/0) и его логика покрыта
  unit-тестами на net8;
- проверка платформы net48 идёт полным сьютом без Agent — это штатная роль сьюта
  («проверка платформы, а не года Revit»);
- если понадобится Agent-прогон на net48: нужен либо отказ от merge STJ для
  IntegrationTests (сломает TUnit), либо `extern alias`-схема + правка тестов на
  `ElementIdCompat` — отдельная задача.

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

## Валидация write-layer (gate `general`) и фикс-проход

Первый вердикт валидатора: **FAIL** — BLOCKER (невалидная JSON-схема `set_view_visibility`)
+ HIGH-дефекты. Всё закрыто до повторной валидации:

| Пункт | Что было | Что сделано |
|---|---|---|
| BLOCKER | `set_view_visibility` InputSchemaJson не парсился | схема перегенерирована; `AgentToolRegistry.ValidateDefinition` парсит схемы всех инструментов на старте и падает с именем инструмента; тест `WriteTools_InputSchemas_AreValidJsonObjects` |
| HIGH | CancellationToken после commit мог отменить запись change set | персист/`MarkRolledBackAsync` только с `CancellationToken.None` |
| HIGH | частичные изменения `configure_views`/`copy_view_settings` не попадали в change set | `AddChangeItems` + `AffectedCount++` на failure-пути; `all_items_failed` только при `AffectedCount==0`; warning о partial |
| HIGH | rename swap/cycle не работал (preflight отклонял, temp-имена теряли OldValue) | `AgentRenamePlanner.Build` (head-first порядок, temp для циклов), валидация с учётом освобождаемых имён, OriginalName в change item; undo откатывает rename-ы пакетно через тот же плейнер |
| HIGH | crop shape нельзя было откатить | `ViewCropState.ShapeMm` (полигон в мм), `ApplyCropState` снимает shape до CropBox, `ApplyCropShape` включает CropBoxActive/Visible + `IsCropRegionShapeValid` |
| HIGH | `set_parameters` Double без unit писал внутренние единицы молча | отказ `invalid_value` с подсказкой; `unit="raw"` — явный opt-in; no-op по `RawValue`; type-scope change item ссылается на type element |
| HIGH | remap: неизвестная категория → молча пустой фильтр | `unknown_category`, display-name категорий, лимит 20 mapping, confirmation count учитывает `filter.limit` |
| HIGH | смена LLM-провайдера переносила старый ключ | `Apply` при смене провайдера грузит ключ целевого из env/secrets.env или очищает; тест-сим `StoredKeyProvider`, 4 unit-теста |
| MEDIUM | rename_levels: ложный cascade_conflict (swap, floor plan + RCP) | каскад проверяется по полному accepted-set, конфликт только внутри одного `ViewFamily`; тест `RenameLevels_SwapWithCascadeViews_*` |
| MEDIUM | undo молча пропускал неспланированные rename-ы и мог закрыть набор | неспланированные → `undo_conflict`; undo ничего не откатил и failed нет → `RollBackChangeSetId` (набор закрывается) |
| MEDIUM | temp-имя оставалось после сбоя rename | `RestoreTemporaryNames` + warning с `[Action]` |
| MEDIUM | `AgentViewWriteHelper` 635 / `UndoLastTool` 622 строк | partial-разбивка: `AgentViewWriteHelper.Crop.cs`, `UndoLastTool.Renames.cs` |
| MEDIUM | дубли `viewId`/`levelId` в одном вызове | `duplicate_item` |
| MEDIUM | `configure_views` rename на шаблоне/листе | `view_is_template` / `unsupported_view_type` guard |
| HIGH (второй проход) | пометка change set в обход реального коммита (silent rollback Revit) | в ветке `!committed` базового класса `RollBackChangeSetId = null`; маркировка только при `!dryRun` |
| MEDIUM | «залипание» набора: skipped/уже-восстановленные держали set applied | «уже восстановлено» → skipped до conflict-проверки; набор закрывается, если все items skipped; `undo_partial` только по реальным `failed` |

Осознанно не сделано: rename-секции `configure_views`/`copy_view_settings` не используют
плейнер (swap имён внутри одного вызова — только через `rename_views`); `TryReadCropShape`
поддерживает один loop из прямых сегментов (дуги/несколько loop → warning); undo уровня
не записывает каскадно переименованные виды (Revit переименовывает их обратно сам).
Известное ограничение undo: если старое имя навсегда занято новым элементом
(`undo_conflict`), неименованный `undo_last` будет снова и снова возвращаться к этому
набору — обход через явный `changeSetId` более раннего набора; «abandon change set» API
пока нет.

Проверка после прохода (финальный gate): unit 3368/3368; integration Agent R25
65 pass / 2 skip (67 total); R27 (net10) 65 pass / 2 skip; net48 (Revit 2023)
245 pass / 12 skip; 7 конфигураций — 0 ошибок. Валидатор `general` — **PASS**.

## Долг / что осталось

1. Ручная приёмка в Revit (владелец): write-flow, оверлей подтверждения, undo.
2. **Фаза G** (частично сделано): token_usage persistence ✅, порог компакции —
   `AgentLlmSettings.ContextWindowTokens` (default 200 000) ✅; plan validation L0–L6
   (preflight → critic → digest) **не начата** — это отдельная крупная фича.
3. **Фаза H**: полный R25 integration-сьют, net48-прогон (`-p:RevitVersion=2023`),
   R27-прогон (Revit 2027 установлен), sweep 7 конфигураций, HANDOFF, коммиты, push.
4. Ручная приёмка записи в Revit (владелец, утро): dry-run → Apply → проверка модели →
   `undo_last`.
5. Ручная приёмка UI-оверлея подтверждения (кнопки Apply/Cancel, digest, таймаут).

## Команды

```bash
# сборка
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
powershell -NoProfile -ExecutionPolicy Bypass -File "tools/build-all-configs.ps1"

# полный unit
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25

# полный integration R25 / net48 (без Agent) / R27 Agent
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R27 --framework net10.0-windows -- --treenode-filter "/*/*/*Agent*/*"

# unit (Agent)
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~Agent"

# integration (Agent), класс в 3-м сегменте фильтра
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*Agent*/*"
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentWrite*/*"
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentContextBudget*/*"

# net48 (Revit 2023 установлен)
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023 -- --treenode-filter "/*/*/*Agent*/*"
```

## Итоговый статус фаз

| Фаза | Статус |
|---|---|
| A baseline | ✅ |
| B write-layer (10 tools) | ✅ |
| C confirmation gate + change sets/undo | ✅ |
| D integration + unit тесты write | ✅ |
| E context budget | ✅ |
| F dogfooding + feedback | ✅ (3 прогона, 3→1→0 замечаний) |
| G extras | частично: token_usage ✅, порог компакции ✅; plan validation L0–L6 — нет |
| H финальный sweep | ✅ 7 конфигураций, unit, R25, net48, R27 |

## Ограничения

- Ветка `develop`; push в develop разрешён, в `main` — нет, PR не создавать.
- `build-and-deploy.bat` НЕ запускать (Revit может быть открыт) — деплой утром у владельца.
- Не коммитить `opencode.json` и `server/` (чужая работа), `secrets.env` вне репозитория.
- Ключи не логировать; Exa — только обезличенные технические запросы.
- Write-tools не пишут в workshared central — отказ `workshared_not_supported`.
- `docs/` вне `docs/agent/`, ADR и changelog не трогать до ручной приёмки.
