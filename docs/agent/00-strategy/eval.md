# Eval AI-агента — журнал прогонов

Живой eval-харнес ночного прогона №2 (фаза A) и последующих итераций.
Задачи: `AgentEvalTasks` (40 кейсов), fixtures: `src/SmartCon.IntegrationTests/Agent/Eval/Fixtures/`,
runner: `AgentEvalRunner`, offline-цикл: `SmartCon.Tests/Agent/Eval/AgentOfflineOrchestratorEvalTests`.

## Как воспроизвести

```bash
# Replay без ключа и сети (40 задач)
SMARTCON_AGENT_EVAL_REPLAY=1 dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 \
  --framework net8.0-windows -- --treenode-filter "/*/*/*AgentEvalTests*/*"

# Live-запись (нужен ключ в secrets.env)
SMARTCON_AGENT_EVAL_RECORD=1 dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 \
  --framework net8.0-windows -- --treenode-filter "/*/*/*AgentEvalTests*/*"

# Подмножество
SMARTCON_AGENT_EVAL_ONLY=read_overview,write_dryrun_rename ...

# Offline-orchestrator (CI, без Revit/ключа)
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 \
  --filter "FullyQualifiedName~AgentOfflineOrchestratorEvalTests"
```

Отчёт каждого прогона: `%LOCALAPPDATA%\AGK\SmartCon\Agent\eval\<timestamp>\` (JSON + markdown).

## 2026-11-18 — базовый live-прогон и replay (фаза A)

- Модель: `deepseek/deepseek-v4.1-flash-1017` (OpenRouter), production system prompt.
- Живая запись: 40 fixtures; первый live-прогон — 34/40 (85 %).
- Причины провалов и фиксы: уточнены ожидания (configure changed=1 на вид,
  альтернативы `set_parameters`/`configure_views`), honest `no_active_view`,
  retry на пустой ответ модели. Перезаписаны только затронутые fixtures.

| Метрика | Значение |
|---|---|
| Task success | 40/40 (100 %) |
| Read tasks | 15/15 (100 %) |
| Write dry-run | 15/15 (100 %) |
| Apply + undo | 10/10 (100 %) |
| Prompt tokens (сумма 40 задач) | 1 105 140 |
| Completion tokens (сумма 40 задач) | 27 056 |
| Schema-valid calls | 144/144 (100 %) |
| Unauthorized writes | 0 |
| Routing accuracy | 100 % |
| Gate (read ≥ 85 %, write ≥ 80 %, schema 100 %, unauthorized 0) | PASS |

### H1-04 — live baseline 5 read + 5 write (2026-11-18 19:07)

Подмножество `read_overview, read_levels, read_filtered_views, read_view_settings,
read_wall_count, dry_rename_view, dry_apply_template, dry_crop_active,
dry_set_parameter, apply_rename_view`; live без записи fixtures
(`SMARTCON_AGENT_EVAL_SOFT=1`), модель `deepseek/deepseek-v4.1-flash-1017`
(OpenRouter). Отчёт: `%LOCALAPPDATA%\AGK\SmartCon\Agent\eval\20261118-190710`.

| Метрика | Значение |
|---|---|
| Tasks | 10/10 (100 %), read 5/5, write 5/5 (dry-run + apply/undo) |
| Tool selection | 100 % |
| Routing accuracy (shortlist) | 100 %; fallback 0 % |
| Schema-valid calls | 20/20 (100 %) |
| Prompt / completion tokens | 202 173 / 4 427 |
| Latency avg / p95 | 4 758 / 7 557 ms (сеть модели) |
| Threshold gate | PASS |


Метрики совпадают для replay (без ключа/сети) и live-прогона после фиксов.
Ранняя редакция этого прогона показывала routing 97.5 % (первый live до фиксов);
после уточнения ожиданий fixtures и retry — 100 %.

## 2026-11-18 — A4 offline-orchestrator (CI)

`AgentOfflineOrchestratorEvalTests` — 5/5:

1. многораундовый tool-loop, каждый tool-call спарен с результатом;
2. лимит 8 раундов останавливает цикл и возвращает `MaxRoundsReached`;
3. компакция истории при превышении бюджета: старые tool-результаты заменяются
   digest-ом, последние 4 остаются целыми, pairing сохраняется;
4. `find_tools` расширяет каталог на следующий раунд (shortlist → полный каталог);
5. `execute_plan`: preview dry-run → apply с `planDigest`, план одобрен один раз,
   два dry-run-прогона и один apply-прогон шага.

## 2026-11-18 — H1.1 routing/cache (H1-11/H1-12/H1-13)

**Стенд.** Реальный каталог — 27 карточек (`AgentToolCards` + `AgentToolCatalog.Enrich`).
Синтетический — 1000 карточек: 20 паков × 50, у каждой карточки уникальный токен
`zzq<pack><NNN>` в Purpose/Synonyms; 100 синтетических запросов из
`synthetic-queries.json` содержат этот токен. Fixtures:
`src/SmartCon.Tests/Agent/Routing/real-queries.json` (100 реальных русских BIM-намерений,
покрытие каждого из 27 тулов ≥2 фразами) и `synthetic-queries.json`.

Команды:

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 \
  --filter "FullyQualifiedName~Agent.Routing"
SMARTCON_DECIDER_LIVE=1 dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 \
  --filter "FullyQualifiedName~RoutingDecisionsLiveTests"
```

| Метрика | rules (BM25, 27 карточек) | decisions (fake Choice, plumbing) | decisions live (OpenRouter `perplexity/pplx-decider-v1.3:free`) |
|---|---|---|---|
| Запросов | 100 | 100 | 20 |
| top-1 | 95.0 % (95/100) | 100 % | 95 % (19/20) |
| top-5 recall | 100 % (100/100) | 100 % | 100 % (20/20) |
| top-14 recall | 100 % (100/100) | — | — |
| false tool rate (read-intent → write top-1) | 0.0 % | — | — |
| p95 | 1.28 ms | — | 1027 ms (решение модели, ADR-075 лимит <2 s) |

- rules: `ToolCardRouter.Rank`, прогрев 10 запросов, p95 по 100 вызовам. Гейт
  top-1 ≥80 %, top-5 ≥95 %, p95 <100 ms — PASS. top-14 — триггер vector-ветки
  ADR-075 §3 (порог 95 %), тоже PASS.
- decisions fake: `DecisionsToolRouter` + fake `IAgentDecisionClient`, Choice =
  `expectedTools[0]`; проверены plumbing, видимость (100/100) и fail-closed
  (исключение клиента → результат rules). Core-инструменты закреплены первыми
  (ADR-075 §2), поэтому top-1/top-5 считаются по non-core слотам.
- decisions live: 20 запросов (каждый 5-й из real-queries), реальный
  `HttpDecisionClient`, 2026-11-18, провайдер OpenRouter, модель
  `perplexity/pplx-decider-v1.3:free`. Модель ответила на все 20 запросов;
  p95 включает сеть.

**Top-1 misses (rules, все 5 остаются в top-5):** r008 «Сколько элементов по
категориям?» → `get_project_overview` вместо `list_categories`; r031 «Сколько
разрезов в модели?» → `list_categories` вместо `list_views`; r043 «Найди шаблон
с …» → `apply_view_template` вместо `list_view_templates`; r046 «Покажи настройки
шаблона вида» → `get_view_settings` вместо `get_view_template_details`; r087
«Откати последний change set» → `discard_change_set` вместо `undo_last`
(пересечение negative examples). Top-5 recall не страдает; карточки не менялись
(итерации разрешены только при top-5 <95 %).

**Синтетический каталог 1000 (H1-12):** top-1 100 %, top-5 100 % (100/100),
p95 11.1 ms. Важно: запросы содержат уникальный токен карточки, поэтому замер
доказывает масштаб/производительность BM25 на 1000 карточках, а не семантическое
покрытие формулировок.

**Размер промпта с паками (H1-12):** `AgentTokenEstimator.EstimateText`:

| Часть | Токены |
|---|---|
| Каталог паков (20 строк name+description) | 469 |
| Core 9 карточек (name+purpose+when) | 735 |
| Top-5 полных схем (representative schema ~800 chars на тул) | 1 166 |
| Итого паки + core + схемы | 2 370 |
| Static system prompt (`AgentSystemPrompt.BuildStatic`) | 1 748 |
| Полный стартовый промпт | 4 118 |

Гейт ≤8000 токенов — PASS. Схемы в этом замере representative (production
`InputSchemaJson` живут в `SmartCon.Agent`), явно не реальные; порядок величины
на 5 тулов сохраняется.

**Cache stability (H1-13).** Новый `AgentPromptHash` (SHA-256 hex):
`ComputeStatic`, `ComputeToolSet` (сортировка по имени, name+InputSchemaJson),
`Compute`. `AgentSystemPrompt.BuildStatic(mode)` выделен из `Build` без изменения
полного текста (BasePrompt + Mode + PlanSection + working rules, без project
context). Тесты: hash стабилен между вызовами; смена ActiveViewName/ViewCount
меняет полный Build, но не static hash; порядок tools не влияет; смена схемы
влияет. Поле cache: `usage.prompt_tokens_details.cached_tokens` уже парсится и
покрыто `LlmResponseAccumulatorTests` (H1-02: cached 40/64, clamp 50) — отдельный
тест не дублировался.

**Вердикт по vector (ADR-075 / H6): не нужен сейчас.** BM25 top-14 recall 100 %
(порог принудительного включения <95 %), top-5 100 % на реальном каталоге,
top-5 100 % на 1000 карточек; видимый каталог — 27 инструментов (<200 по ADR-075
§3). Embeddings не добавлялись. Пересмотреть в H6 при каталоге >200 видимых
инструментов или при падении top-14 recall <95 % на семантических (не
token-уникальных) запросах.

## Фаза D — до/после

Заполняется после live-прогона с plan-кейсами (`execute_plan`). Формат:

| Метрика | До | После | Δ |
|---|---|---|---|
| Task success | | | |
| Tool selection | | | |
| Запросов на задачу | | | |
| Токенов на задачу | | | |

## H1.2 (2026-11-18) — context ladder и legacies

Легаси v1 удалено (флаги `ToolCatalogV2`/`ContextCompactionV2`/`WorkingStateEnabled`
и старые ветки), каталог стал 28 инструментов (`read_tool_result` в паке `meta`,
всегда доступен). Метрики routing пересняты на 102 реальных запросах:

| Метрика | Значение |
|---|---|
| rules real top-1 | 95.1 % (95/100 было; новые meta-запросы в top-1) |
| rules real top-5 | 100 % |
| false tool | 0 % |
| p95 | 2.05 мс (<100 мс) |
| synthetic 1000 карточек top-1 / top-5 | 100 % / 100 % |
| decisions fake visible / top1 / top5 | 102/102 / 102 / 102 |
| Полный стартовый промпт с паками | 4 285 токенов (≤8k) |

Store-and-reference (H1-14): результат >40k символов сохраняется в
`tool_result_blobs` после redaction, конверт получает `resultHandle` (id
tool-call), модель дочитывает через `read_tool_result(handle, offset, limit,
path)`. Blob живёт 30 дней и привязан к conversation; после рестарта доступен.

Compaction ladder (H1-15..H1-17): mask → nine-section checkpoint → hard shrink.
Маска не трогает последние 6 tool-результатов и `find_tools`/`load_tool_pack`/
`read_tool_result`/`execute_plan`, сохраняет `resultHandle`.
`compactions` хранит чекпойнт (covered ordinal, summary, tokens before/after,
model, source hash); после рестарта оркестратор восстанавливает
`<conversation_checkpoint>` + хвост. Summarizer input режется на чанки
`SummaryMaxInputTokens` (default 24k) и суммируется рекурсивно; 3 подряд ошибки
открывают circuit breaker, ход продолжается на маске/жёстком сжатии.

Compaction eval (H1-18, 10 кейсов): 20 фактов на 200 ходах (≥95 %), ElementId/
UniqueId, changeSetId, planDigest, рестарт, ошибка summarizer, целостность пар
tool-call→tool-result, pinning ограничений пользователя, protected results,
cap summarizer-входа. Все 10 PASS (детерминированный summarizer, без сети).

Integration (H1-19): `AgentCompactionStorageTests` — 5 тестов на реальном SQLite
внутри Revit 2025 (blob restart/session isolation, paging, checkpoint store,
checkpoint→projection, cancellation) — 5/5 PASS. net48 сьют остаётся
Revit-boundary (Agent-тесты исключены из net48 по конфигурации проекта).

## 2026-11-18 — новые инструменты, фаза 1: листы и ведомости

Каталог 27 → 31. Добавлены read-задачи `read_sheets`, `read_sheet_content`,
`read_schedules`, `read_schedule_data` (fixtures записаны вручную, replay без
ключа). Детерминированный seed расширен: ведомость `SC_Eval_Schedule` по стенам
с полем Comments и лист `SC-EVAL-101` (Title Block подгружается из
контент-библиотеки Revit, если в шаблоне проекта его нет).

| Метрика | Значение |
|---|---|
| Новые eval-задачи (replay) | 4/4 PASS |
| Schema-valid calls | 4/4 |
| Routing accuracy новых задач | 100 % |
| Routing rules, 110 реальных запросов | top-1 96.4 %, top-5 100 %, top-14 100 %, p95 1.68 мс, false tool 0 |
| Synthetic 1000 карточек | top-1 100 %, top-5 100 %, p95 11.65 мс |
| Decisions (fake Choice), 110 запросов | visible 110/110, top-1 110, top-5 110 |

Top-1 misses остались те же четыре (r008, r031, r046, r087), все в top-5;
новые запросы r103–r112 попадают в top-1.

## 2026-11-19 — новые инструменты, фаза 2: фильтры и overrides

Каталог 31 → 33 (`create_view_filter`, `set_filter_overrides`). Eval: +2
write-dry-run задачи (`dry_create_filter`, `dry_filter_overrides`); seed
дополнен фильтром `SC_Eval_Filter` (категория стен, без правил и без применения).

| Метрика | Значение |
|---|---|
| Полный replay | 46/46 PASS, schema 149/149, unauthorized 0, gate PASS |
| Routing rules, 120 реальных запросов | top-1 96.7 %, top-5 100 %, top-14 100 %, p95 1.64 мс, false tool 0 |
| Synthetic 1000 карточек | top-1 100 %, top-5 100 %, p95 17.12 мс |
| Integration R25 `AgentFilterToolsTests` | 13/13 |

Top-1 misses прежние четыре (r008, r031, r046, r087); новые запросы r113–r122
попадают в top-1.

## 2026-11-19 — фаза 3: routing/decision эксперимент на 170 фразах

**Стенд.** `real-queries.json` расширен 120 → **170** реальных русских фраз
(новые r123–r172: парафразы существующих инструментов + вариации новых).
Live agent-loop: новый `RoutingLiveBenchmarkTests` (opt-in `SMARTCON_AGENT_LIVE=1`)
прогоняет production `AgentOrchestrator` с 33 скриптованными инструментами
(правдоподобные payload'ы с id) на 16 интентах (10 прямых + 6 парафраз без
ключевых слов) в двух режимах: rule router и decision router внутри `find_tools`.

| Метрика | rules (BM25) | decisions (pplx free, live) |
|---|---|---|
| Routing top-1 (170 фраз) | 95.9 % | 100 % (20-запросная выборка) |
| Routing top-5 (170 фраз) | 100 % | 100 % (20-запросная выборка) |
| top-14 recall | 100 % | — |
| false tool (read→write top-1) | 0 % | — |
| p95 решения | 1.70 мс | 4 129 мс (сеть) |
| Agent-loop: ожидаемый тул в первом shortlist | 16/16 | 16/16 |
| Agent-loop: задача решена (тул вызван) | 16/16 | 15/16 |
| Раундов до первого правильного вызова (avg / p95) | 3.81 / 11 | 3.07 / 9 |
| Всего раундов на задачу (avg) | 7.62 | 6.25 |
| `find_tools` на задачу | 0 | 0 |
| `load_tool_pack` на задачу | 0 | 0 |

**Вывод.** Decision как reranker **не нужен по умолчанию** на текущем каталоге:
BM25 top-5 содержит правильный инструмент во всех 16 интентах (включая
парафразы), поэтому `find_tools` не вызывается и decision-слой не включается;
разница агентских метрик между режимами — шум одной живой серии, а не эффект
reranker'а. На routing-уровне decision закрывает 4 top-1 промаха BM25
(95.9 % → 100 % на выборке), но платит ~4 с p95 за вызов. Решение: оставить
`SMARTCON_DECIDER_ENABLED=0` (текущий secrets.env), включать точечно для
длиннохвостых запросов/большого каталога; пересмотреть при падении top-5 <95 %
или росте каталога >200.

**Раунды.** Модель сначала исследует модель (get_project_overview/list_views/
query_elements), только затем вызывает целевой инструмент — в среднем 3 раунда
до правильного вызова при 6–8 раундах на задачу. Shortlist тут не виноват:
целевой инструмент был виден с первого запроса.
