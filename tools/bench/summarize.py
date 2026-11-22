"""Summarize benchmark JSON artifacts into a per-case model comparison (markdown).

usage: python -X utf8 summarize.py <results-root> [--base synthetic|template]
"""
import glob
import json
import os
import sys
from collections import defaultdict

root = sys.argv[1]
only_base = None
if "--base" in sys.argv:
    only_base = sys.argv[sys.argv.index("--base") + 1]

runs = defaultdict(list)  # (model, base, caseNumber) -> artifacts (latest wins)
for path in glob.glob(os.path.join(root, "*", "*", "*", "*", "*.json")):
    try:
        data = json.load(open(path, encoding="utf-8-sig"))
    except Exception as ex:  # noqa: BLE001
        print("unreadable:", path, ex)
        continue
    key = (data.get("model") or "?", data.get("base") or "?", int(data.get("caseNumber") or 0))
    runs[key].append(data)

def latest(items):
    return sorted(items, key=lambda d: d.get("startedUtc") or "")[-1]

models = sorted({k[0] for k in runs})
bases = sorted({k[1] for k in runs if only_base is None or k[1].startswith(only_base)})
cases = sorted({k[2] for k in runs})

def fmt(d):
    if d is None:
        return "—"
    v = (d.get("verdict") or {}).get("label") or "?"
    score = (d.get("verdict") or {}).get("score")
    return (f"{v} {score:.1f} · {int(d.get('durationSeconds') or 0)}s · r{d.get('rounds')} · "
            f"c{d.get('toolCalls')} · {d.get('stopReason')}")

for base in bases:
    print(f"\n### base = {base}")
    print("| кейс | " + " | ".join(models) + " |")
    print("|---|" + "---|" * len(models))
    for case in cases:
        if not any(k[1] == base and k[2] == case for k in runs):
            continue
        cells = []
        for model in models:
            items = runs.get((model, base, case))
            cells.append(fmt(latest(items)) if items else "—")
        print(f"| {case} | " + " | ".join(cells) + " |")

    print("\n| модель | PASS | PARTIAL | FAIL | среднее время | средние раунды | средние вызовы | completion-токены (sum) | reasoning-токены (sum) |")
    print("|---|---|---|---|---|---|---|---|---|")
    for model in models:
        chosen = [latest(v) for k, v in runs.items() if k[0] == model and k[1] == base]
        if not chosen:
            continue
        labels = [((d.get("verdict") or {}).get("label")) for d in chosen]
        n = len(chosen)
        avg = lambda f: sum(float(d.get(f) or 0) for d in chosen) / n  # noqa: E731
        print(f"| {model} | {labels.count('PASS')} | {labels.count('PARTIAL')} | {labels.count('FAIL')} | "
              f"{avg('durationSeconds'):.0f}s | {avg('rounds'):.1f} | {avg('toolCalls'):.1f} | "
              f"{sum(int(d.get('completionTokens') or 0) for d in chosen)} | "
              f"{sum(int(d.get('reasoningTokens') or 0) for d in chosen)} |")

print("\n### ошибки по кодам (model: code = n)")
for model in models:
    codes = defaultdict(int)
    for k, v in runs.items():
        if k[0] != model:
            continue
        for code, n in (latest(v).get("errorCallsByCode") or {}).items():
            codes[code] += int(n)
    print(f"- {model}: " + (", ".join(f"{c} = {n}" for c, n in sorted(codes.items(), key=lambda x: -x[1])) or "нет"))
