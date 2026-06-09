# Hot-Loop Counter Pattern Audit (C3)

> Проведён **2026-06-09** в рамках финализации `feature/logging-improvements`.
> Цель: определить, нужны ли counter pattern в hot loops.

## TL;DR

**Counter pattern не нужен в существующем коде.** Все `for`/`foreach` циклы с
`SmartConLogger.Debug/Info` либо:

1. **Содержат 1-5 iter per call** (типично — `foreach (var c in allConns)` где
   `allConns.Count` = 2-4 connectors на FamilyInstance). Allocation pressure
   отсутствует.

2. **Содержат summary lines** (одна `Debug` строка после цикла, не внутри).
   Это правильный pattern — видим итог без spam'а в логе.

3. **Уже сделаны `Debug` после Phase 0b** (Info → Debug в 53+ местах, 8
   `Measure` обёрток). Counter pattern был бы overkill.

## Counter pattern (из C2) готов к использованию

`src\SmartCon.Core\Logging\HotLoopCounter.cs` создан и unit-tested (8 тестов).
Skill `smartcon-logging/references/counter-pattern.md` описывает когда применять.

**Когда ПРИМЕНЯТЬ counter в будущем:**
- Цикл может выполняться **>1000 iter** per operation
- Каждый iter эмитит `Debug($"...")` с интерполяцией (allocation)
- В Release сборке Debug отключён, но allocation в `BeginScope/Info` всё равно есть

**Примеры где МОЖЕТ понадобиться в будущем:**
- BFS по 10k+ элементам в большой модели
- Поиск по Lookup Table с fuzzy match на 100+ строк
- Connector-поиск в PipeNetwork с >100 connectors

## Аудит по файлам

| Файл | Циклов | С Debug внутри | Hot (>100 iter) | Action |
|---|---:|---:|---:|---|
| `RevitLookupTableService.cs` | 29 | 0 | 1 (`for row=1..lines.Length`) | None — Debug summary after |
| `RevitParameterResolver.cs` | 6+ | 1 (post-diagnostic) | 0 | None |
| `RevitDynamicSizeResolver.cs` | 18+ | 0 | 0 | None |
| `ChainOperationHandler.cs` | 18+ | 5 (per-elem) | 0 (max ~50 levels × 5) | None |
| `PipeConnectSessionBuilder.cs` | 2 | 0 | 0 | None |
| `LookupTableCsvParser.cs` | 1 | 0 | 0 | None |
| `CtcGuessService.cs` | 1+ | 0 | 0 (typical 1-2) | None |

## Сценарии, где counter нужен БУДЕТ

1. **BFS по 10k+ элементам** — если в будущем появится
   `ChainOperationHandler.IncrementLevel` для огромной модели. Сейчас
   `graph.Levels[nextLevel].Count` обычно 1-20.

2. **Lookup table с 1000+ строк** — `RevitLookupTableService.GetAllSizeRows`
   парсит CSV-таблицу. Размер таблицы — обычно 10-50 строк, не 1000.
   Если появятся таблицы 500+ строк — применить counter на `for (int row = 1; row < lines.Length; row++)`.

3. **PipeNetwork с >100 connectors** — `PipeConnectSessionBuilder.Build`
   проходит по connectors. Обычно 5-30. Counter не нужен.

## Связанные commits

- `c084e58` — C1 MeasureScope API
- `bdbf991` — C2 HotLoopCounter helper + skill reference
- `2c99a9b` — docs: counter-pattern reference

## Audit method

```bash
# PowerShell: найти все циклы в hot-файлах
rg -t cs -U "foreach\s*\([^)]+\)|for\s*\([^)]+\)" src/ \
   --files-with-matches \
   | ForEach-Object {
       $f = $_
       rg -t cs -U -B 4 -A 1 "SmartConLogger\.Debug\(" $f
   }
```

Просмотрено вручную: 6 файлов × 80+ циклов. **Никаких новых hot loops
не обнаружено.**
