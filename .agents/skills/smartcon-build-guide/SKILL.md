---
name: smartcon-build-guide
description: "SmartCon Revit plugin build guide. Multi-version build configurations (R19/R21/R24/R25/R26/R27), correct restore/build commands, CI/CD workflow, branch protection. Triggers on: build, deploy, release, compile, dotnet build, configuration. Keywords: SmartCon, Revit, build, deploy, release, R19, R21, R24, R25, R26, R27, net48, net8, net10, restore, CI/CD."
license: MIT
metadata:
  author: AGK Engineering
  version: "1.2.0"
---

# SmartCon Build & Deploy Guide

Multi-version Revit plugin build configurations and CI/CD workflow.

## Quick Reference

| Config | Revit | TFM | Command |
|---|---|---|---|
| `Debug.R27` | 2027 | net10.0-windows | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27` |
| `Debug.R26` | 2026 | net8.0-windows | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26` |
| `Debug.R25` | 2025 | net8.0-windows | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` |
| `Debug.R24` | 2024 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24` |
| `Debug.R21` | 2021-2023 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21` |
| `Debug.R19` | 2019-2020 | net48 | `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19` |

**Tests:** `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25`
(the Tests project is pinned to net8.0-windows — do NOT build it under R27: NU1201/NU1202;
in the sln its R27 configurations are mapped to R25, honored only in VS)

**Важно:** таблица выше — деплой-сборки `SmartCon.App` (весь граф + WPF + ILRepack).
Для итерации по модулю App не собирать: `dotnet build
src/SmartCon.<Module>/SmartCon.<Module>.csproj -c Debug.R25 -f net8.0-windows` — см. §6.

**All 6 configs in parallel (no deploy):**
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1`
(used by `build-and-deploy.bat`; per-config logs in `%TEMP%\smartcon-build`)

**SDK:** `global.json` — floor 10.0.100, rollForward latestPatch (**10.0.1xx band ONLY**;
the 10.0.2xx band has WPF build regressions — see errorMessage in global.json) — ALL
configurations (including net48) build under SDK 10.
The .NET 8 RUNTIME is still required to run unit tests.

## Build vs Deploy

### Intermediate Build (Development/Testing)
Build individual configurations for quick testing during development. Does NOT deploy to Revit.
Fastest full check — all 6 in parallel, no deploy:
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1`
```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19
```

### Full Build + Deploy (Production Ready)
**ONLY use after user has manually tested changes in Revit.** This builds ALL configurations AND copies files to Revit addins folders.
```bash
build-and-deploy.bat
```
**WARNING:** Do NOT run `build-and-deploy.bat` during development. Use individual `dotnet build` commands instead.

## CRITICAL Rules (Agents Forget These!)

### 1. Build ALL Versions, Not Just R25
When making changes that affect compilation (new APIs, #if directives, project files):
```bash
# Build ALL 6 shipping configurations (for verification, NOT deploy)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19

# FASTEST — all 6 in parallel (same runner as build-and-deploy.bat)
powershell -ExecutionPolicy Bypass -File tools\build-all-configs.ps1

# OR for final deploy (only after user testing!)
build-and-deploy.bat
```

Every compilation-affecting change must build ALL 6 configurations — there is no
"configuration in work" and no postponing the sweep to "before commit". Incremental
build (§6) keeps each configuration from doing redundant work, but the sweep itself
is mandatory. Configurations are isolated (`obj\<Config>\` + per-config
`project.assets.json`), so they can and should be built in parallel — never run two
builds of the SAME configuration concurrently.

### 2. Restore Requires Configuration
```bash
# WRONG — fallback to RevitAPI 2021.*, false errors CS0618
dotnet restore src/SmartCon.App/SmartCon.App.csproj

# CORRECT — configuration tells restore which RevitAPI version
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R25
```

### 3. TFM Switch = NEVER Use `--no-restore`

Each configuration has its **own** NuGet graph:
`obj\<Configuration>\project.assets.json` (per-config `MSBuildProjectExtensionsPath`,
see `src/Directory.Build.props`). A `-c` switch cannot reuse another configuration's
assets — the target configuration still needs its own restore for its RevitAPI year
(2020.* / 2021.* / 2024.* / 2025.* / 2026.* / 2027.*).

`build-and-deploy.bat` / `tools\build-all-configs.ps1` run `dotnet build` WITHOUT
`--no-restore` for every configuration, so each one gets a clean assets file.

```bash
# WRONG — switching -c without restore: the R21 graph may be absent or stale.
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21 --no-restore
#                            ^^^^^^^^^^^^^^^^^^^^^^ NEVER do this

# CORRECT — restore is part of every build; per-config assets stay isolated
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19

# FASTEST — the same six builds, parallel
powershell -ExecutionPolicy Bypass -File tools\build-all-configs.ps1
```

**Heuristic for the agent:** `--no-restore` is acceptable only for the SECOND build
of the **same** configuration in a row (e.g. after fixing a typo). Switching `-c`
always requires a fresh restore.

### 4. Build App Project, NOT Solution
```bash
# WRONG — pulls extra TFM
dotnet build src/SmartCon.sln -c Debug.R25

# CORRECT
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
```

### 5. BG1002 "*.baml cannot be found" = IDE/CLI race over obj/ — retry, don't reconfigure
Root cause proven (#281): CLI builds race background design-time builds of the VS Code C#
extension (Roslyn LanguageServer) over the shared `obj/` (the dotnet/wpf#4299 mechanism;
also msbuild#6170). NOT a config error and NOT SDK-band-specific (A/B: 10.0.201 vs 10.0.112 —
fails on both). Fix ladder: (1) re-run `dotnet build` — usually enough; (2) if it sticks
(LS keeps building in background) — close/reload the VS Code window (or kill
`Microsoft.CodeAnalysis.LanguageServer` + `dotnet build-server shutdown`), then
`git clean -xfd src` and rebuild; (3) full rebuilds and `build-and-deploy.bat` — run with
VS Code closed. Prevention: `files.watcherExclude` for `**/obj/**` / `**/bin/**` in the
local `.vscode/settings.json`.

### 6. Incremental Build / Fast Loop (главное правило)

**Замерено 2026-11-19 (Debug.R25, `--no-restore`):**

| Сценарий | Время |
|---|---|
| no-op сборка `SmartCon.Revit` (модуль агента) | **1 с** |
| повторная сборка `SmartCon.App` (steady state) | **2 с** |
| первая сборка App после паузы | **27 с** |
| холодная сборка одной конфигурации с нуля | **3–5 мин** |
| 7 конфигураций подряд (sweep) | **20–30 мин** |

Инкрементальный билд исправен. «10 минут на сборку» — это холодная сборка
конфигурации/App и/или sweep, а не обычная итерация по модулю.

**Правила (для любого модуля и любого агента):**

1. **Один конфиг на итерацию.** Разработка — `Debug.R25`. `Debug.R23` (net48) и
   `Debug.R27` (net10) — только если правка реально условно-компилируемая, по
   одному прогону. Полный sweep R19–R27 — только релизный гейт/CI, не после
   каждой правки.
2. **Собирать только затронутый модуль:**
   `dotnet build src/SmartCon.<Module>/SmartCon.<Module>.csproj -c Debug.R25 -f net8.0-windows --no-restore`
   (`--no-restore` — только когда per-config assets уже созданы). `SmartCon.App`
   тянет весь граф, WPF XAML и ILRepack: в цикле разработки модуля он запрещён,
   App собирается один раз на деплой/финальный гейт.
3. **После первой сборки — только `--no-build`** для `dotnet run` / `dotnet test`.
   `--filter` не ускоряет сборку — он только фильтрует выполнение.
4. **Один запуск Revit на пакет тестов**, а не на тест: старт хоста ~1.5–2 мин.
   Несколько классов — объединять в один `--treenode-filter`.
5. **UI-итерации — без пересборки App и без рестарта Revit:** RevitAddInManager /
   RevitDevTool (collectible AssemblyLoadContext, Revit 2025+) грузят новую
   сборку по F5 прямо в открытом Revit.
6. **Если сборка внезапно стала медленной — не перезапускать вслепую:**
   `dotnet build ... -bl:build.binlog` и искать в логе `Building target ...
   completely`, `is newer than output`, `Target Performance Summary`. Это покажет
   конкретный таргет и конкретный входной файл.
7. Каждая конфигурация имеет изолированные `obj\<Configuration>\` и свой
   `project.assets.json`: переключение `-c` не перезаписывает чужой граф.
   ILRepack-merge кэшируется в `obj/<Config>/<TFM>/merged/` и переиспользуется,
   пока входы байт-идентичны (pre-merge dll, `project.assets.json`,
   `ILRepack.targets`). Не удалять этот каталог без причины — это форсирует re-merge.
8. `Debug.*` пропускает NET-анализаторы и IDE code style
   (`EnableNETAnalyzers=false`, `EnforceCodeStyleInBuild=false`); генераторы и
   пакетные анализаторы остаются. `Release.*` (CI, `release.ps1`) — полный набор.
9. Первая сборка после checkout / `dotnet clean` / `git clean` — полная (~3–5 мин
   на конфигурацию); повторная без изменений — 1–2 с.
10. Кросс-конфигурационная проверка (R23/R27) — в конце задачи или в CI, не в
    каждой итерации. Разные конфигурации можно собирать параллельно
    (`tools\build-all-configs.ps1`), одну и ту же — никогда.
11. Antivirus: исключить репозиторий, `%USERPROFILE%\.nuget\packages`,
    `%LOCALAPPDATA%\NuGet\v3-cache`, `%TEMP%\NuGetScratch`, процессы `dotnet.exe` /
    `VBCSCompiler.exe`. Без исключений полные сборки в разы медленнее (Kaspersky
    измерен на этом репозитории).

## CI/CD

- **Branch protection:** `main` requires PR → squash-merge. NEVER push to main directly.
- **NO PR without user request:** Agent commits to current branch only. PR only if user says "merge to main".
- **Dependabot frozen packages:** `Microsoft.Extensions.DependencyInjection` 8.x, `System.Text.Json` 8.x
- **Release:** `tools\release.bat` (local only, CI validates tags)

## References

- [Build Configurations](references/build-configurations.md) — Full multi-version build guide
- [CI/CD Workflow](references/ci-cd-workflow.md) — GitHub Actions, branch protection, release
