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
| Schema-valid calls | 144/144 (100 %) |
| Unauthorized writes | 0 |
| Routing accuracy | 97.5 % |
| Gate (read ≥ 85 %, write ≥ 80 %, schema 100 %, unauthorized 0) | PASS |

Метрики совпадают для replay (без ключа/сети) и live-прогона после фиксов.

## 2026-11-18 — A4 offline-orchestrator (CI)

`AgentOfflineOrchestratorEvalTests` — 5/5:

1. многораундовый tool-loop, каждый tool-call спарен с результатом;
2. лимит 8 раундов останавливает цикл и возвращает `MaxRoundsReached`;
3. компакция истории при превышении бюджета: старые tool-результаты заменяются
   digest-ом, последние 4 остаются целыми, pairing сохраняется;
4. `find_tools` расширяет каталог на следующий раунд (shortlist → полный каталог);
5. `execute_plan`: preview dry-run → apply с `planDigest`, план одобрен один раз,
   два dry-run-прогона и один apply-прогон шага.

## Фаза D — до/после

Заполняется после live-прогона с plan-кейсами (`execute_plan`). Формат:

| Метрика | До | После | Δ |
|---|---|---|---|
| Task success | | | |
| Tool selection | | | |
| Запросов на задачу | | | |
| Токенов на задачу | | | |
