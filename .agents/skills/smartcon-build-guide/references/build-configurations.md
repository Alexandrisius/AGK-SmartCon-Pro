# Мульти-версионная сборка SmartCon

## Конфигурации

| Конфигурация | Покрывает Revit | TFM | RevitAPI NuGet | shipping ZIP |
|---|---|---|---|---|
| `Release.R19` | 2019–2020 | net48 | 2020.* | `SmartCon-X.X.X-R19.zip` |
| `Release.R21` | 2021–2023 | net48 | 2021.* | `SmartCon-X.X.X-R21.zip` |
| `Release.R24` | 2024 | net48 | 2024.* | `SmartCon-X.X.X-R24.zip` |
| `Release.R25` | 2025 | net8.0-windows | 2025.* | `SmartCon-X.X.X-R25.zip` |
| `Release.R26` | 2026 | net8.0-windows | 2026.* | `SmartCon-X.X.X-R26.zip` |
| `Release.R27` | 2027 | net10.0-windows | 2027.* | `SmartCon-X.X.X-R27.zip` |

**Правило (per-version бинарники, с #233):** каждая версия Revit в цепочке 2025+ получает
СВОЮ shipping-конфигурацию и СВОЙ бинарник (R25→2025, R26→2026, R27→2027). Прежняя схема
«один R25-бинарник для 2025+2026» сломала бы #233: в API 2026 у `WireType.WireMaterial` /
`TemperatureRating` / `Insulation` сменились типы на `ElementId`, у `MaxSize` — на `string`;
старый бинарник на Revit 2026 = `MissingMethodException`. Revit 2027 — .NET 10
(официально Autodesk: «All add-ins must be built against the .NET 10 SDK»), поэтому R27 = net10.0-windows.
Revit 2026.5+ тоже на .NET 10 — net8-бинарник R26 работает там через runtime roll-forward
(R26 остаётся net8, целевые версии 2026.0–2026.4).
В net48-цепочке группировка сохранена: соседние версии без `#if`-разделения и с одинаковыми
`DefineConstants` по-прежнему собираются в одну shipping-конфигурацию (R19→2019-2020, R21→2021-2023).

**Когда добавлять отдельный shipping-архив:**
- Появился `#if REVIT20XX_OR_GREATER` или аналог, разделяющий две версии
- Revit API внёс breaking change (изменение сигнатур, удаление методов)
- Разные TFM (net48 vs net8.0-windows vs net10.0-windows)

## Полная сборка + деплой — `build-and-deploy.bat`

```bash
build-and-deploy.bat
```

Скрипт собирает 6 shipping-конфигураций (R27/R26/R25/R24/R21/R19) + updater и деплоит в Revit.
Шесть конфигураций собираются **параллельно** через `tools\build-all-configs.ps1`
(безопасно: у каждой свой `obj\` и свой `obj\<Configuration>\project.assets.json`).
Без деплоя та же параллельная сборка запускается так:
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1`.
Запускать `build-and-deploy.bat` только перед ручным тестом/сдачей.
Каждая версия 2025+ получает СВОЙ бинарник: R25→2025, R26→2026, R27→2027 (per-version, #233).

## Почему НЕЛЬЗЯ использовать `-p:RevitVersion=...`

Пакет `Nice3point.Revit.Api.RevitAPI` использует `VersionOverride` зависящий от `$(RevitVersion)`.
При `dotnet build ... -p:RevitVersion=2025` restore НЕ видит `RevitVersion` из конфигурации
→ fallback на RevitAPI `2021.*` для net48 → API 2022+ недоступно → ошибки компиляции.

Именованные конфигурации (`Debug.R25`) парсят `RevitVersion` из имени в `Directory.Build.props`
**до** restore → каждая сборка получает правильную версию RevitAPI.

## Ручная сборка одной конфигурации

**ВАЖНО:** Каждая конфигурация — отдельный `dotnet build` (НЕ solution — он подтянет лишние TFM).
Конфигурации изолированы (`obj\<Configuration>\` + собственный `project.assets.json`), поэтому их
можно и нужно собирать параллельно — для этого есть `tools\build-all-configs.ps1`.
Любая правка плагина проверяется сборкой ВСЕХ 6 конфигураций — понятия «конфигурация в работе» нет
(SKILL.md §1). После успешной сборки нужной конфигурации тесты/запуски гоняй с `--no-build` (SKILL.md §6).

**КРИТИЧЕСКИ ВАЖНО:** `dotnet restore` без указания конфигурации пишет assets в `obj/`
(Debug-fallback), а не в `obj\<Configuration>\`, и не парсит `RevitVersion` из имени
конфигурации — это даёт fallback на RevitAPI 2021.* и ложные ошибки компиляции
(CS0618 и др.) для R24/R25/R26/R27. Всегда собирай через `dotnet build -c Debug.RXX`
(implicit restore в правильный per-config путь).

**Правильный способ — restore + build в ОДНОЙ команде (без `--no-restore`):**

```bash
# 1. net8.0-windows (Revit 2026)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26

# 3. Затем net8.0-windows (Revit 2025)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# 4. Затем net48 (Revit 2024)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24

# 5. Затем net48 (Revit 2021-2023) — implicit restore выполняется внутри build
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21

# 6. Затем net48 (Revit 2019-2020) — implicit restore выполняется внутри build
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19
```

**Если ТОЧНО нужен отдельный restore:**
```bash
# НЕВЕРНО: dotnet restore без -p:Configuration не знает RevitVersion!
# dotnet restore src/SmartCon.App/SmartCon.App.csproj  ← НЕ ДЕЛАЙТЕ ТАК

# ВЕРНО: передаём Configuration как MSBuild property
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24 --no-restore
```

## Правила сборки

1. Собирай `SmartCon.App.csproj`, а НЕ `SmartCon.sln`
2. Каждую конфигурацию — отдельной командой; разные конфигурации можно параллельно,
   одну и ту же — нельзя
3. **При переходе между разными TFM (net8 ↔ net48 ↔ net10) — ВСЕГДА используй `dotnet build`
   без `--no-restore`** (implicit restore в per-config `obj\<Configuration>\project.assets.json`)
4. **`global.json` пинит .NET SDK 8.0.4xx (`rollForward: latestFeature`)** — причина: любая сборка
   SDK 10 этого решения в 10–20 раз медленнее (dotnet/roslyn#81704: в компиляторе SDK 10 выключен
   tiered compilation; фикс 10.0.300+ скорость не возвращает — 148 с против 5–19 с холодной R25).
   Revit 2027 / net10 выключен флагом `SmartConEnableRevit2027=false` (`src/Directory.Build.props`):
   `-c Debug.R27` падает с явной ошибкой. Вернуть: флаг в true + global.json на полосу 10.0.4xx.
   BG1002 (*.baml not found) — гонка CLI-сборки с фоновыми design-time сборками VS Code
   (Roslyn LanguageServer) за общий obj/ (dotnet/wpf#4299), НЕ связана с бэндом SDK:
   повторить сборку; при залипании — закрыть VS Code, `git clean -xfd src`, пересобрать (#281).

## Тесты

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

Проект тестов пиннут на net8.0-windows — НЕ собирай его под R27 (net10 → NU1201/NU1202).
В sln его R27-конфигурации замапены на R25 (маппинг honored только в VS).

**Debug vs Release:** `Debug.*` собираются без NET/IDE-анализаторов
(`EnableNETAnalyzers=false`, `EnforceCodeStyleInBuild=false` — быстрый цикл);
`Release.*` (CI, `release.ps1`) — полный набор. Генерируемый IL одинаков.

## Чеклист перед коммитом

1. `build-and-deploy.bat` — 0 ошибок, 0 предупреждений на всех конфигурациях
2. Тесты — 0 падений
3. Инварианты I-01..I-17 не нарушены
