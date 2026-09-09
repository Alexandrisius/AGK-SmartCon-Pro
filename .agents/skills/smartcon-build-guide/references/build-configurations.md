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

## ЕДИНСТВЕННЫЙ правильный способ сборки — `build-and-deploy.bat`

```bash
build-and-deploy.bat
```

Скрипт собирает 6 shipping-конфигураций (R27/R26/R25/R24/R21/R19) + updater и деплоит в Revit.
Каждая версия 2025+ получает СВОЙ бинарник: R25→2025, R26→2026, R27→2027 (per-version, #233).

## Почему НЕЛЬЗЯ использовать `-p:RevitVersion=...`

Пакет `Nice3point.Revit.Api.RevitAPI` использует `VersionOverride` зависящий от `$(RevitVersion)`.
При `dotnet build ... -p:RevitVersion=2025` restore НЕ видит `RevitVersion` из конфигурации
→ fallback на RevitAPI `2021.*` для net48 → API 2022+ недоступно → ошибки компиляции.

Именованные конфигурации (`Debug.R25`) парсят `RevitVersion` из имени в `Directory.Build.props`
**до** restore → каждая сборка получает правильную версию RevitAPI.

## Ручная сборка одной конфигурации

**ВАЖНО:** Собирай каждую конфигурацию ОТДЕЛЬНО. НЕ собирай solution — он подтянет лишние TFM.

**КРИТИЧЕСКИ ВАЖНО:** `dotnet restore` без указания конфигурации НЕ парсит `RevitVersion`
из `Directory.Build.props` (там `$(Configuration)` = Debug по умолчанию). Это приводит к
fallback на RevitAPI 2021.* и ложным ошибкам компиляции (CS0618 и др.) при сборке R24/R25/R26/R27.

**Правильный способ — restore + build в ОДНОЙ команде (без `--no-restore`):**

```bash
# 1. Сначала net10.0-windows (Revit 2027)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R27

# 2. Затем net8.0-windows (Revit 2026)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R26

# 3. Затем net8.0-windows (Revit 2025)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# 4. Затем net48 (Revit 2024)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24

# 5. Затем net48 (Revit 2021-2023) — тот же TFM, restore не нужен
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21

# 6. Затем net48 (Revit 2019-2020) — тот же TFM, restore не нужен
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
2. Каждую конфигурацию — отдельной командой
3. **При переходе между разными TFM (net8 ↔ net48 ↔ net10) — ВСЕГДА делай
   `dotnet restore` с `-p:Configuration=...` перед сборкой, ИЛИ используй `dotnet build`
   без `--no-restore`**
4. **`global.json` пинит SDK 10.0.100 (rollForward latestPatch)** — ВСЕ конфигурации
   (включая net48) собираются под SDK 10. SDK 8 для сборки больше не нужен, но .NET 8
   RUNTIME требуется для запуска юнит-тестов (`SmartCon.Tests` = net8.0-windows).

## Тесты

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

Проект тестов пиннут на net8.0-windows — НЕ собирай его под R27 (net10 → NU1201/NU1202).
В sln его R27-конфигурации замапены на R25 (маппинг honored только в VS).

## Чеклист перед коммитом

1. `build-and-deploy.bat` — 0 ошибок, 0 предупреждений на всех конфигурациях
2. Тесты — 0 падений
3. Инварианты I-01..I-17 не нарушены
