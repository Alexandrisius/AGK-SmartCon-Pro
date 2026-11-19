# Prompt cache: стабильный префикс и cache-aware контекст — план реализации

> **Статус:** active · 2026-11-19 · владелец: владелец продукта.
> **Базируется на:** `harness-ideal-plan.md` §3.1, §6.1, §6.2, P0-0…P0-2, P1-1…P1-7; `autonomy-roadmap.md`.
> **Заменяет/уточняет:** P0-1 и P0-2 в части фактического состояния кода и provider-specific деталей.
> **Для агента с нулевым контекстом:** прочитай этот файл целиком, затем §3 (карта кода) и §4 (решения).
> Не начинай менять код, пока не снят baseline из §6, фаза 0.

## 1. Зачем это нужно

Провайдерский prompt cache — это **точное совпадение префикса токенов**. Промпт рендерится как `tools → system → messages`. Если изменился токен на позиции N, кеш действителен для `0..N-1` и пересчитывается для `N..end`. Поэтому:

- добавление нового сообщения/файла **в конец** — дёшево (старый префикс cache read, новый хвост — full price);
- любое изменение **в начале/середине** (system, tools, старое сообщение) — дорого: пересчёт всего после изменения;
- кешированные токены всё равно занимают контекстное окно;
- кеш best-effort, не гарантия; модель/усилие/провайдер меняют кеш.

### 1.1 Провайдеры (факты для реализации)

| Провайдер | Как включается | Минимум | TTL | Запись / чтение | Важное |
|---|---|---|---|---|---|
| Anthropic | `cache_control` breakpoints (до 4) или top-level automatic | 512–4096 в зависимости от модели | 5 мин / 1 ч | 1.25× / 2× запись; 0.1× чтение (иногда 0.05×/0.025×) | изменение tools инвалидирует tools+system+messages; system — system+messages; lookback 20 блоков |
| OpenAI | автоматически; GPT-5.6+ — явные breakpoints | 1024 токена | 30 мин / 12 ч (GPT-5.6+); 5–10 мин / 24 ч (старые) | 1.25×/2.5× запись (GPT-5.6+), иначе без записи; чтение ~0.1–0.5× | `prompt_cache_key` для routing (старые модели); tools — часть префикса |
| DeepSeek | автоматически, диск | нет опубликованного минимума | часы–дни, best effort | чтение ~0.02–0.03× от miss | нужен **полный** матч сохранённого prefix unit; `A+B` → `A+B+C` hit, `A+B` → `A+C` — нет |
| Gemini | неявно (2.5+), явные кеши | 2048 / 4096 | 3–5 мин (implicit) | ~0.25× или 90% скидка | `systemInstruction` для кеша неизменяем; динамику — в позднее user-сообщение |
| OpenRouter | прокидывает кеш провайдера; sticky routing | зависит от провайдера | зависит | зависит | sticky routing по хешу первого system и первого non-system; 60 мин простоя |
| OpenCode Go | OpenAI/Anthropic-совместимые маршруты; `x-opencode-session` | зависит | зависит | cached read/write в прайсе моделей | стабильный session id **на диалог** для routing/кеша |

OpenCode (для ориентира): Anthropic-маркеры ставятся на первые два system и последние два non-system сообщения (≤4 breakpoint, `packages/opencode/src/provider/transform.ts:359-405`); OpenAI-подобным передаётся `promptCacheKey = sessionID` (там же, `:1261-1274`). Компакция сохраняет хвост ~15k токенов и отправляет полный транскрипт + инструкцию суммаризации в конце, чтобы запрос суммаризатора попал в кеш; позже summary обновляется по дельте. При слишком длинном транскрипте OpenCode откатывается в «сжатый текстовый» режим — и тогда кеш теряется (известный баг-паттерн, `anomalyco/opencode#63373`).

### 1.2 OpenCode v2: что именно делает source (срез `anomalyco/opencode`, dev, v2.3.0)

Важно: `sst/opencode` теперь редиректит на `anomalyco/opencode`; layout v2 другой. Локального форка в нашем репозитории нет: `.opencode/` — это плагин-конфиг и планы, не исходники. Ниже — то, что реально стоит перенять:

| Механизм | Где | Что делает | Что берём |
|---|---|---|---|
| System split | `packages/core/src/session/model-request.ts` (`baseTranscript`) | system = статичный agent/provider prompt + baseline инструкций | два уровня: static system + session baseline |
| Порядок контекста | `packages/core/src/session/context.ts:190-192` | user catalog/guidance → project instructions → date/environment; baseline остаётся reusable prefix | «общее → изменчивое» |
| Заморозка baseline | `session/instruction-state.ts:143-146`; `session/message-updater.ts:158-165` | baseline не перегенерируется; изменения — delta-сообщением (`notice: "instructions"`) | mode/plan/память — только delta в хвосте |
| Tool order | `packages/core/src/tool.ts:367-372` | сортировка по имени; code-mode tool последним | canonical sort |
| Schema determinism | `tool/runtime.ts:206-209`; `test/tool-schema.test.ts:209` | стабильный JSON key order, без safe-int границ; schema не меняется | каноническая сериализация схем |
| Tool removal | `session/runner/llm.ts:279` | на финальном шаге не убирает tools, а ставит `toolChoice: "none"` | не менять tools — отключать вызовом |
| Cache policy | `packages/ai/src/cache-policy.ts:28-35,58-83`; `route/client.ts:555` | `auto`: last tool + first/last system + conversation tail, cap 4; Anthropic/Bedrock/OpenRouter/Vertex — маркеры; Gemini/OpenAI implicit — без inline | provider adapter + cap 4 |
| Anthropic breakpoints | `anthropic-messages.ts:1026-1030,541-550` | порядок tools → system → messages, `cache_control` ephemeral + ttl | breakpoints в нашем payload |
| OpenAI key | `openai-chat.ts:853-861`; `protocols/shared.ts:110-123`; `session/model-request.ts:325-336` | `prompt_cache_key` ≤64, срез `ses_`, headers affinity | ключ = conversationId, стабильный |
| DeepSeek | `openai-chat.ts:965-985` | implicit, ключ не шлётся, читает `prompt_cache_hit_tokens` | не шлём ключ, читаем usage |
| Gemini | `cache-policy.ts:81-83` | только implicit, без inline-маркеров (tail-маркер писал бы новый кеш каждый шаг) | не двигать breakpoint каждый шаг |
| Compaction | `packages/core/src/session/compaction.ts:1146-1159,430-459,998-1044,338-343` | ceiling = window − max(15%, 16k); tail 15k с якорем на user; summary отправляет весь конверс + prompt для cache hit; готовая summary удерживается до due | порог/хвост/summary-префикс |
| Fallback | `compaction.ts:698-777,1015-1020` | если не влезает: history без хвоста, затем flatten в текст с `tools: []` — кеш теряется | принять как осознанный fallback |
| Usage | `openai-chat.ts:965-985`; `tui/src/routes/session/rows.ts:425-437` | cache read/write, hit rate; допускает колебания 1–2 бакета | метрики по шагам |

Вывод: OpenCode уже реализует ровно тот каркас, который нам нужен: замороженный baseline, детерминированные tools, provider-specific cache policy, summary-запрос с тем же префиксом. Наш план ниже — адаптация этого каркаса к .NET/OpenAI-compatible клиенту и нашим инструментам.

## 2. TL;DR — что сделать

1. **Сначала измерение:** per-request cache-метрики (cached/cache_write/cost/staticHash/toolHash) в лог и usage. Не менять поведение, пока не снят baseline.
2. **Стабильный префикс:** `BuildStatic()` без mode/plan/document; session-context — отдельным сообщением; turn-context — отдельным **персистентным** сообщением на ход; никаких переписываний user-сообщений.
3. **Заморозить tools на диалог:** core + начальный shortlist + загруженные; каноническая сортировка по имени; `load_tool_pack` меняет набор только на границе хода и логирует инвалидацию; убрать per-turn top-5 из payload.
4. **Session id на диалог:** `x-opencode-session = conversationId`; для OpenAI — `prompt_cache_key`; для Anthropic через OpenRouter — `cache_control`.
5. **Cache-aware компакция:** порог ближе к окну; запрос суммаризатора использует тот же system+tools+history, инструкция — в конце; keep-хвост; учитывать usage суммаризатора.
6. **Метрики и UI:** cache hit rate, cost per turn, breakdown; алерт при падении.

## 3. Текущее состояние кода (карта для изменений)

Все пути — от `src/`.

| Область | Файл:строка | Что делает | Проблема для кеша |
|---|---|---|---|
| Turn start | `SmartCon.Agent/Services/AgentOrchestrator.cs:91` | снимает project context раз в ход | волатильные поля идут в system |
| System prompt | `SmartCon.Core/Models/Agent/AgentSystemPrompt.cs:63-81` | `BuildPrefix(mode)` + `context.BuildPromptSection()` + rules | project context в середине; mode/plan в статике; `BuildStatic()` не используется в production |
| Project context | `SmartCon.Core/Models/Agent/AgentProjectContext.cs:47-67` | title/id/path/version/units/IsModified/active view/counts/TopCategories | `IsModified`, active view, counts, TopCategories меняются → system переписывается |
| Project provider | `SmartCon.Revit/Agent/AgentProjectContextProvider.cs:84-151` | полный скан модели для top-8 категорий; кеш 15 с по DocumentVersion | дорого и волатильно; ключ кеша меняется на каждое изменение модели |
| Tool selection | `SmartCon.Agent/Services/AgentOrchestrator.Tools.cs:201-300` | core 9 + loaded + top-5 по текущему userText; порядок — recency | набор и порядок меняются каждый ход/round |
| Tool send | `SmartCon.Agent/Services/Llm/LlmPayloadBuilder.cs:18-21,124-137` | tools в порядке selection, без canonical sort | другой порядок → другой префикс |
| Turn context | `SmartCon.Agent/Services/AgentOrchestrator.WorkingState.cs:76-88` | вставляет `<turn_context>` + `<progress round/...>` в **последнее user-сообщение** | переписывает сообщение в середине на каждом round → инвалидация |
| Context status | `SmartCon.Agent/Services/AgentContextStatus.cs:15-55` | отдельное system-сообщение в конце | ок (tail), но дублирует progress |
| History compaction | `SmartCon.Agent/Services/AgentContextCompactor.cs:83-278` | mask → summary → hard shrink; 0.7 от (window-reserve), 0.75 projection, 20k min savings | переписывает старые tool-results; summary-запрос не делит префикс с основным |
| Summarizer | `SmartCon.Agent/Services/Llm/LlmAgentSummarizer.cs:44-56` | отдельный запрос: static instruction + transcript | не использует тот же system+tools, usage не пишется → кеш/стоимость невидимы |
| Legacy compactor | `SmartCon.Agent/Services/AgentHistoryCompactor.cs:15-64` | 0.6/keep 4 (fallback, тесты) | агрессивнее основного |
| LLM request | `SmartCon.Core/Models/Agent/AgentChatRequest.cs:3-14` | model/messages/tools/temperature/max | нет provider options/cache полей |
| Headers | `SmartCon.Agent/Services/Llm/OpenAiCompatibleLlmClient.cs:28,137-143` | `x-opencode-session` один на клиент | не на диалог; cache_control/prompt_cache_key отсутствуют |
| Usage parse | `SmartCon.Agent/Services/Llm/LlmResponseAccumulator.cs:202-234` | cached/cache_write парсятся | ок |
| Usage store | `SmartCon.Agent/Services/Storage/SqliteAgentUsageStore.cs:28-67`; `AgentSchema.cs:218-253` | колонки cached/cache_write/request_id/compaction_id есть | одна строка на ход; rounds не видны, `CompactionId` не заполняется |
| Prompt hash | `SmartCon.Core/Services/Agent/AgentPromptHash.cs:13-58` | `ComputeStatic`/`ComputeToolSet` | используются только в тестах |

## 4. Решения, которые уже приняты (не переобсуждать)

- **Стабильный префикс — закон:** static system + отсортированные tools впереди, всё волатильное — в turn-context в хвосте (`harness-ideal-plan` §3.1, §6.2).
- **Turn-context — отдельное сообщение перед последним user** (`§6.2`); не переписывать user-сообщение.
- **Tools фиксируются на сессию** (P0-1, P3-10); `find_tools` — только discovery, `load_tool_pack` меняет набор на границе хода.
- **Cache accounting:** `cached_tokens`/`cache_write_tokens` уже в схеме и парсере; нужно писать/читать и показывать.
- **Компакция:** маскировать, только если экономия ≥20k токенов; checkpoint + tail; store-and-reference уже есть.
- **ADR по prompt cache нет.** Если этот план принимается как архитектурное решение — создать `docs/adr/078-prompt-cache-stable-prefix.md` (фаза 0).

**Факты, которые нельзя путать (проверено 2026-11-19):**
- В каталоге **55 карточек** + `load_tool_pack` = **56 зарегистрированных** `IAgentTool`; `docs/agent/README.md:14` и `autonomy-roadmap.md` всё ещё говорят «45» — устарело, поправить в фазе 0.
- Локального форка OpenCode в репозитории **нет**: `.opencode/` — конфиг/планы, `server/` — не OpenCode. Research делался по upstream `anomalyco/opencode` (dev, v2.3.0).
- Коллизия «v4» в документах (audit/events vs jobs) к этому плану отношения не имеет; не менять нумерацию миграций без сверки с `autonomy-roadmap.md`.

## 5. Целевая архитектура

### 5.1 Слои промпта

```
[ static system        ]  роль, правила, I-инварианты, tool policy; НЕ меняется в диалоге
[ static tools         ]  замороженный набор, каноническая сортировка по имени
[ session context      ]  title/documentId/RevitVersion/units/workshared; один раз на диалог
[ conversation history ]  только append; ничего не переписываем
[ turn context         ]  date, active view, selection, mode, plan, counts; один раз на ход
[ latest user message  ]  как есть
[ per-round status     ]  round/elapsed/context tokens; последнее сообщение, не персистится
```

Правила:
- **Никогда** не менять system/tools/старые сообщения в рамках диалога. Mode/plan меняются — это delta-сообщение в хвосте, не правка системы.
- Turn context **персистится** (строка в `messages` с ролью system и скрытием в UI), иначе на следующем ходу он исчезнет из префикса и кеш порвётся. Per-round status — не персистится, всегда последний.
- Session context персистится один раз на диалог; при смене документа — новый диалог/сброс.
- `IsModified` **не нужен** модели: убрать из промпта. TopCategories — только в `get_project_overview` или в turn context (но не в static).

### 5.2 Заморозка tools

- Новое поле в `AgentSessionContext`: `FrozenToolNames` + `ToolSetHash`.
- В первом ходу: `FrozenToolNames = core + initial shortlist + loaded` (shortlist по первому сообщению; если пусто — только core).
- В каждом следующем ходу: definitions строятся из `FrozenToolNames`, **сортируются по имени**; порядок не зависит от recency/`TouchTool`.
- `load_tool_pack` добавляет имена в `FrozenToolNames` и применяет на границе хода; лог: `tool set changed: oldHash → newHash, cache invalidated`.
- `find_tools` не меняет payload.
- `ToolSetMode` (config): `FrozenShortlist` (по умолчанию) или `FullCatalog` (если `EstimateTools(all) < 10–15% контекстного окна`).
- **Детерминизм схем:** сериализовать JSON schema с фиксированным key order, без timestamp/GUID/локали в описаниях; один и тот же набор tools должен давать байт-идентичный блок. Добавить тест `tool schema serialization is stable`.
- **Не убирать tools на финальном шаге.** Если нужно запретить вызовы — ставить `toolChoice: "none"` (OpenCode `session/runner/llm.ts:279`), а не удалять определения.
- `AgentPromptHash.ComputeToolSet` использовать в production для лога/hash, а не только в тестах.

### 5.3 Session/cache key и provider policy

- `AgentChatRequest` получает `ConversationId`, `CacheKey`, `ProviderOptions`.
- `x-opencode-session = "ses_" + conversationId` (на запрос, не на процесс); `x-session-affinity`/`X-Session-Id` — по возможности тот же id (OpenCode `session/model-request.ts:325-336`).
- Ввести `ProviderCachePolicy` (аналог OpenCode `packages/ai/src/cache-policy.ts`):
  - `Auto` для Anthropic/Bedrock/OpenRouter-Anthropic: breakpoints в порядке `tools → system → messages`, максимум 4; ставить на последний tool, первый/последний system и хвост conversation; `cache_control: {type:"ephemeral", ttl?}`; TTL 5m/1h.
  - OpenAI: `prompt_cache_key = conversationId`, обрезать до 64 символов, без префикса `ses_`; implicit; опционально `prompt_cache_retention`.
  - DeepSeek: **ничего не слать** (implicit), читать `prompt_cache_hit_tokens`/`prompt_cache_miss_tokens`.
  - Gemini: только implicit, **не** ставить inline-маркеры и не двигать tail-breakpoint каждый шаг.
  - OpenRouter: auto-маркеры только для `anthropic/*` (для остальных — NONE); sticky routing не ломать сменой system.
  - Qwen/Alibaba: explicit `cache_control`, если модель поддерживает (OpenCode ставит для qwen).
- При смене модели/усилия — лог `cache namespace changed`; метрики сбрасываются.
- Если gateway режет/удаляет маркеры — fallback без них, лог, не считать кеш гарантией.

### 5.4 Cache-aware компакция

- Порог: OpenCode использует `ceiling = window − max(15%, 16k)` и срабатывает при `measured + estimated ≥ ceiling` (`compaction.ts:1146-1159,430-439`). Для 500k это ~425k, а не 350k. Текущий 0.7 от (window−reserve) — раньше; измерить и поднять к 0.80–0.85 (не 0.6).
- Хвост: **15k токенов с якорем на user-сообщение** (OpenCode default `keep=15000`, `compaction.ts:264-266,998-1044`) либо текущий `TailTokens` (15% usable, 25k–150k) — выбрать одно по замеру, не смешивать.
- Запрос суммаризатора: **тот же** system + tools + history (тот же префикс), инструкция суммаризации — **последним сообщением**. Тогда запрос суммаризатора читает кеш (Claude Code/OpenCode). Если не влезает — fallback: history без хвоста, затем flatten в текст с `tools: []`; это осознанная потеря кеша, логировать.
- Usage суммаризатора писать в `token_usage` с `request_id`/`status`, иначе стоимость и cache hit невидимы.
- Masking: сохранять последние 6 tool-results и protected tools; min savings 20k оставить; добавить economics-guard: если `savings_tokens * priceCached < recache_cost` — не маскировать.
- Background precompute (OpenCode `compaction.ts:338-343`): считать summary заранее, но **не применять**, пока не наступил порог; проверять возраст кеша (`CACHE_LIFETIME=5m`/`cache.ttl`). Опционально, после стабилизации.
- Entity-dedupe (семейства/виды) — **не в этом плане**. OpenCode не дедуплицирует повторные чтения; если делать, только в tail и при экономии > стоимости re-cache.

## 6. План по фазам

### Фаза 0 — baseline и метрики (1 день, без изменения поведения)

- [ ] Создать `docs/adr/078-prompt-cache-stable-prefix.md` (решение: стабильный префикс, заморозка tools, cache-aware компакция) — если владелец согласен.
- [ ] В `AgentOrchestrator` на каждый LLM-ответ логировать: `round`, `request_id`, `prompt_tokens`, `cached_tokens`, `cache_write_tokens`, `cost`, `staticHash`, `toolSetHash`, `messageCount`, `toolCount`. Использовать `AgentPromptHash.ComputeStatic/ComputeToolSet`.
- [ ] `CompactionId` заполнять в `AgentUsageRecord` (сейчас всегда null).
- [ ] Прогнать 5 диалогов по 10–20 ходов на реальном провайдере; записать baseline: hit rate, cost/turn, staticHash/toolHash изменения.
- [ ] Записать baseline в этот файл (раздел «Журнал») или `eval.md`.
- [ ] Docs hygiene: исправить «45 тулов» в `docs/agent/README.md`, `autonomy-roadmap.md`, `night-run-3.md` (актуально 55 каталожных + `load_tool_pack`).

**Приёмка:** в логе есть cached/miss по каждому запросу; baseline зафиксирован; поведение не изменилось.

### Фаза 1 — стабильный префикс (2–3 дня)

- [ ] `AgentSystemPrompt`: `BuildStatic()` (роль/правила/инварианты/tool policy), `BuildSessionContext(projectContext)` (title/id/version/units/workshared), `BuildTurnContext(turnState)` (date/active view/selection/mode/plan/counts). `BuildStatic` не содержит mode/plan/document. `Build` больше не склеивает всё в один system.
- [ ] `AgentProjectContext`: убрать `IsModified` из промпта; TopCategories — только по запросу (`get_project_overview`) или в turn context, не в static.
- [ ] `AgentOrchestrator.RunTurnAsync`: system = `BuildStatic()`; session context — один раз на диалог, персистится; turn context — один раз на ход, персистится; per-round status — в конце, не персистится.
- [ ] `ApplyTurnContext`: удалить переписывание user-сообщения; заменить на добавление отдельного сообщения. Не дублировать `<progress>` и `<context_status>`.
- [ ] `AgentSessionContext`/`AgentOrchestrator.Tools`: `FrozenToolNames`, `FrozenToolSetHash`; выбор один раз; canonical sort по имени в `LlmPayloadBuilder` (или перед ним); убрать per-turn top-5 из payload; `load_tool_pack` — на границе хода + лог инвалидации.
- [ ] `AgentChatRequest` + `OpenAiCompatibleLlmClient`: `ConversationId`; `x-opencode-session` на диалог; `prompt_cache_key` для OpenAI-подобных; `cache_control` для Anthropic-маршрутов OpenRouter.
- [ ] `AgentPromptHash` использовать в production; логировать `staticHash`/`toolSetHash`; тест: hash не меняется при смене active view/mode (mode — delta).

**Приёмка:**
- unit: 10 ходов без изменений → `staticHash` и `toolSetHash` одинаковы; смена active view/mode не меняет staticHash; turn context — последнее сообщение перед user; tools отсортированы.
- live: второй и последующие запросы дают `cached_tokens > 0`; первый — cache write/miss.
- routing eval: top-1 ≥80%, top-5 ≥95% (не хуже baseline).

### Фаза 2 — cache-aware компакция (2–3 дня)

- [ ] `LlmAgentSummarizer`: строить запрос из того же system+tools+history; инструкция — последним сообщением; писать usage суммаризатора.
- [ ] `AgentContextCompactor`: порог 0.80–0.85 (замерить); mask только при economics-guard; keep tail 15k (или TailTokens — выбрать одно); не компактить при 60%.
- [ ] Лог: `compaction before/after tokens`, `cache hit on summarizer`, `savings vs recache cost`.
- [ ] Тест: суммаризатор получает тот же префикс (hash совпадает с основным запросом), кроме последней инструкции; usage записан.

**Приёмка:** compaction-запрос имеет cache hit >0; стоимость хода после компакции не выше, чем при keep-all на 10 ходов (замер).

### Фаза 3 — метрики/UI/алерты (1–2 дня)

- [ ] `token_usage`: добавить `cache_hit_rate` (или вычислять в SQL/UI), `rounds`, `compaction_id` (заполнять).
- [ ] UI: header «tokens / cost / cache %», клик — breakdown; контекст: system/tools/history/summary.
- [ ] Алерт: hit rate < 50% на 5 запросах → WARN с причиной (static/tool hash change, compaction, model switch).
- [ ] Eval: 5 кейсов — same prefix; active-view change; tool change; model change; compaction. Живые тесты — gated.

## 7. Что НЕ делать

- Не переписывать старые сообщения «для красоты» — только компакция на границе.
- Не менять tools в середине диалога без `load_tool_pack` и лога.
- Не включать turn context в static system.
- Не делать entity-dedupe, который переписывает префикс каждый ход (только tail и по экономии).
- Не строить карту модели в system prompt (ADR-077).
- Не полагаться на кеш как на гарантию: всегда считать полную стоимость и иметь fallback.
- Не смешивать два разных значения tail (15k vs 15% usable) в одном коде.

## 8. Риски и откат

| Риск | Митигация |
|---|---|
| Заморозка tools ухудшает routing | A/B на eval; `ToolSetMode=FullCatalog` для больших окон; `find_tools` + `load_tool_pack` как escape hatch |
| Провайдер не поддерживает кеш / gateway режет `cache_control` | feature-detect; лог; fallback без маркеров; не считать кеш гарантией |
| Изменение модели/усилия сбрасывает кеш | лог `cache namespace changed`; не смешивать метрики |
| Компакция дороже, чем keep-all | economics-guard; порог около окна; keep-all для дешёвого cached input |
| DeepSeek не даёт hit из-за неполного prefix unit | только append; не менять середину; полный матч |
| Turn context не персистится | персистить один раз на ход; per-round status — последним |
| Mode/plan в static ломает префикс | mode/plan — delta в хвосте |
| OpenRouter sticky routing меняет бакет | стабильный system/session message; `prompt_cache_key`/session id |
| Смена диалога/рестарт | новый session id; кеш холодный — нормально; frozen set восстановить из БД/заново |

## 9. Команды и проверки

```bash
# сборка модулей (R25)
dotnet build src/SmartCon.Agent/SmartCon.Agent.csproj -c Debug.R25
dotnet build src/SmartCon.Revit/SmartCon.Revit.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# unit
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~SmartCon.Tests.Agent"

# routing eval
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --no-build --filter "FullyQualifiedName~RoutingEvalTests"

# live 10–20 ходов (нужен ключ; пример — существующий live harness)
# SMARTCON_AGENT_LIVE=1 ... (см. docs/agent/agent-live-test-harness)
```

Проверять в логе `smartcon.log`:
- `staticHash` / `toolSetHash` — не меняются без причины;
- `cached_tokens` / `cache_write_tokens` по каждому запросу;
- `compaction` — before/after, cache hit суммаризатора;
- `cache namespace changed` при смене модели.

## 10. Ссылки

- Anthropic prompt caching: https://platform.claude.com/docs/en/build-with-claude/prompt-caching
- Anthropic context editing: https://platform.claude.com/docs/en/build-with-claude/context-editing
- OpenAI prompt caching: https://developers.openai.com/api/docs/guides/prompt-caching
- DeepSeek context caching: https://api-docs.deepseek.com/guides/kv_cache/
- OpenRouter prompt caching: https://openrouter.ai/docs/guides/best-practices/prompt-caching
- OpenCode compaction: https://opencode.ai/docs/compaction/
- OpenCode warming: https://opencode.ai/docs/warming/
- OpenCode Go (session id/pricing): https://opencode.ai/docs/go/
- OpenCode source (upstream `anomalyco/opencode`, dev): `packages/core/src/session/model-request.ts`, `packages/core/src/session/context.ts`, `packages/core/src/session/compaction.ts`, `packages/ai/src/cache-policy.ts`, `packages/ai/src/protocols/anthropic-messages.ts`, `packages/ai/src/protocols/openai-chat.ts`
- Claude Code prompt caching: https://code.claude.com/docs/en/prompt-caching
- Клод-код разбор кеша (community): https://claude.dev/blog/lessons-from-building-claude-code-prompt-caching-is-everything/
- Codex prompt caching notes: https://rememorio.github.io/blog/codex/en/prompt-cache-performance/
- Cache-aware compaction economics: https://www.nikibirkner.com/ai-lessons/compaction-economics/
- `clear_at_least` экономика: https://dreaming.press/posts/tune-clear-at-least-context-editing-prompt-cache.html

## 11. Журнал baseline (заполнить в фазе 0)

| Дата | Модель/провайдер | Ходов | cached/prompt | cache write | cost/turn | staticHash менялся | toolHash менялся | Комментарий |
|---|---|---|---|---|---|---|---|---|
| — | — | — | — | — | — | — | — | — |