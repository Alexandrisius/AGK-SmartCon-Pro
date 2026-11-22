#!/bin/bash
# Live-прогон real-cases бенчмарка через ПРОДУКЦИОННЫЙ цикл агента (AgentOrchestrator в
# headless-хосте). Одна «полоса» = один собранный worktree + один провайдер/модель +
# список кейсов подряд в отдельных хостах Revit.
#
# Полосы из РАЗНЫХ worktree можно запускать параллельно (несколько моделей одновременно).
# Два хоста из одного worktree одновременно падают — не делать.
#
# usage: lane.sh <worktree> <lane-name> <provider> <model> <synth|template> <CaseM_Live...>
#   provider: zai-glm | opencode-go | openrouter (ключи берёт AgentLlmSettings из окружения/secrets.env)
#   CaseM_Live: тест-метод кейса (Case6..Case15 = кейсы 1..10 SSOT)
# env (опционально):
#   SMARTCON_BENCH_RESULTS_DIR  каталог артефактов (JSON + транскрипт); по умолчанию %LOCALAPPDATA%\Temp\smartcon-bench\results
#   SMARTCON_BENCH_TEMPLATE     путь к .rvt — база кейсов = копия реального файла (без него — синтетический сид)
#   SMARTCON_BENCH_MAX_ROUNDS   лимит раундов на кейс (по умолчанию в харнессе 48)
#   SMARTCON_BENCH_MAX_SECONDS  лимит времени на кейс (по умолчанию в харнессе 900)
#   SMARTCON_BENCH_LOG_DIR      каталог логов полосы
set -u
WT="$1"; LANE="$2"; PROVIDER="$3"; MODEL="$4"; BASE="$5"; shift 5

LOG_DIR="${SMARTCON_BENCH_LOG_DIR:-$HOME/smartcon-bench-logs}"
mkdir -p "$LOG_DIR"
if [ -z "${SMARTCON_BENCH_RESULTS_DIR:-}" ]; then
  export SMARTCON_BENCH_RESULTS_DIR="$(cygpath -w "$HOME")\\AppData\\Local\\Temp\\smartcon-bench\\results"
fi

cd "$WT" || { echo "worktree not found: $WT" >&2; exit 2; }
export SMARTCON_AGENT_LIVE=1 SMARTCON_LLM_PROVIDER="$PROVIDER" SMARTCON_AGENT_MODEL="$MODEL"
if [ "$BASE" = "template" ]; then
  [ -n "${SMARTCON_BENCH_TEMPLATE:-}" ] || { echo "SMARTCON_BENCH_TEMPLATE is not set" >&2; exit 2; }
  export SMARTCON_BENCH_TEMPLATE
fi

for CASE in "$@"; do
  TAG="$LANE-$BASE-$CASE"
  echo "=== START $TAG $(date +%H:%M:%S)"
  dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows --no-build -- \
    --treenode-filter "/*/*/*AgentRealCasesBenchmarkTests*/*${CASE}*" > "$LOG_DIR/$TAG.log" 2>&1
  echo "=== END $TAG exit=$? $(date +%H:%M:%S) $(grep -aE 'всего|сбой|успешно|пропущено' "$LOG_DIR/$TAG.log" | tr -s ' \n' ' ')"
done
echo "LANE DONE $LANE $(date +%H:%M:%S)"
