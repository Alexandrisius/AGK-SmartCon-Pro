# Мульти-версионная сборка SmartCon

## Конфигурации

| Конфигурация | Покрывает Revit | TFM | RevitAPI NuGet | shipping ZIP |
|---|---|---|---|---|
| `Release.R19` | 2019–2020 | net48 | 2020.* | `SmartCon-X.X.X-R19.zip` |
| `Release.R21` | 2021–2023 | net48 | 2021.* | `SmartCon-X.X.X-R21.zip` |
| `Release.R24` | 2024 | net48 | 2024.* | `SmartCon-X.X.X-R24.zip` |
| `Release.R25` | 2025–2026 | net8.0-windows | 2025.* | `SmartCon-X.X.X-R25.zip` |
| `Release.R26` | 2026 | net8.0-windows | 2026.* | **CI-only** (валидация) |

**Правило:** Если две соседние версии Revit не имеют `#if`-разделения в коде и компилируются
с одинаковыми `DefineConstants` — они группируются в одну shipping-конфигурацию.
Старшая версия остаётся как CI-only конфигурация для раннего обнаружения breaking changes в API.

**Когда добавлять отдельный shipping-архив:**
- Появился `#if REVIT20XX_OR_GREATER` или аналог, разделяющий две версии
- Revit API внёс breaking change (изменение сигнатур, удаление методов)
- Разные TFM (net48 vs net8.0-windows)

## ЕДИНСТВЕННЫЙ правильный способ сборки — `build-and-deploy.bat`

```bash
build-and-deploy.bat
```

Скрипт собирает 4 конфигурации (R25/R24/R21/R19) + updater и деплоит в Revit.
R25-бинарник копируется в папки Revit 2025 и 2026 (один бинарник для обеих версий).

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
fallback на RevitAPI 2021.* и ложным ошибкам компиляции (CS0618 и др.) при сборке R24/R25.

**Правильный способ — restore + build в ОДНОЙ команде (без `--no-restore`):**

```bash
# 1. Сначала net8.0-windows (Revit 2025-2026)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# 2. Затем net48 (Revit 2024)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24

# 3. Затем net48 (Revit 2021-2023) — тот же TFM, restore не нужен
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21

# 4. Затем net48 (Revit 2019-2020) — тот же TFM, restore не нужен
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
3. **При переходе между разными TFM (net8 → net48 или net48 → net8) — ВСЕГДА делай
   `dotnet restore` с `-p:Configuration=...` перед сборкой, ИЛИ используй `dotnet build`
   без `--no-restore`**

## Тесты

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

## Чеклист перед коммитом

1. `build-and-deploy.bat` — 0 ошибок, 0 предупреждений на всех конфигурациях
2. Тесты — 0 падений
3. Инварианты I-01..I-17 не нарушены
