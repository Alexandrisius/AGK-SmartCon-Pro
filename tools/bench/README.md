# Live-бенчмарк реальных кейсов через прод-цикл агента

Кейсы: `docs/agent/real-cases-benchmark-v2.md` (SSOT), код: `src/SmartCon.IntegrationTests/Agent/AgentRealCasesBenchmarkTests.*`.
Каждый live-кейс гоняет **тот же агент, что видит пользователь в Revit** (`AgentOrchestrator` в headless-хосте
`ProductionAgentHost`: прод-промпт, паки и скиллы, бюджеты, LoopGuard, компакция). Пользовательское сообщение —
только дословный текст кейса.

## Один кейс

```bash
SMARTCON_AGENT_LIVE=1 SMARTCON_LLM_PROVIDER=zai-glm SMARTCON_AGENT_MODEL=glm-5.3 \
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- \
  --treenode-filter "/*/*/*AgentRealCasesBenchmarkTests*/*Case6_Live*"
```

`Case6`…`Case15` = кейсы 1…10. Ключи — как в проде (`secrets.env` / окружение), значения не печатаются.

## Полоса (несколько кейсов, одна модель)

```bash
bash tools/bench/lane.sh <worktree> <lane> zai-glm glm-5.3 synth Case6_Live Case7_Live Case8_Live Case9_Live Case10_Live
bash tools/bench/lane.sh <worktree> <lane> opencode-go deepseek-v4.1-flash synth Case11_Live Case12_Live Case13_Live Case14_Live Case15_Live
```

Несколько моделей — параллельно, по одной полосе на **отдельный собранный worktree** (`git worktree add`).
Два хоста Revit из одного worktree одновременно не запускать.

## База: синтетика или реальный шаблон

`SMARTCON_BENCH_TEMPLATE=<путь к .rvt>` — база кейса становится копией реального файла (оригинал не трогается).
Сиды кейсов 5, 6, 7, 8, 10 встают на него без конфликтов; кейсы 1, 2, 3, 9 требуют доработки сидов.
Проверка без LLM: тот же фильтр с `_SeedVerifies`.

## Артефакты

`SMARTCON_BENCH_RESULTS_DIR` (по умолчанию `%LOCALAPPDATA%\Temp\smartcon-bench\results`):
`<provider>/<model>/<base>/<caseN>/r<время>.json` + `.md` (транскрипт) + `.db` (база сессии агента:
messages/tool_calls/audit_log — открывается любым SQLite-браузером; `SMARTCON_BENCH_KEEP_DB=0` отключает).
В JSON: вердикт по состоянию модели,
время, раунды, вызовы тулов (по именам и по одному), токены (в т.ч. reasoning и кэш), `stopReason`, ошибки по кодам.

Сводная таблица по модели:

```bash
python -X utf8 tools/bench/summarize.py "<SMARTCON_BENCH_RESULTS_DIR>" [--base synthetic]
```

## Бюджеты кейса

`SMARTCON_BENCH_MAX_ROUNDS` (48) и `SMARTCON_BENCH_MAX_SECONDS` (900) — защиты от зависания, не лимиты продукта;
при превышении `stopReason = round_budget | time_budget`, вердикт не выше PARTIAL.
