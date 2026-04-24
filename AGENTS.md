Отвечай всегда на русском языке.

# SmartCon — Системная инструкция для AI-агентов

Ты работаешь над проектом **SmartCon** — плагином для Autodesk Revit 2025 (.NET 8 / C# 12 / WPF).
Флагманский модуль — **PipeConnect**: соединение трубных MEP-элементов в 3D двумя кликами.

Исходный код расположен в `src/`. Solution: `src/SmartCon.sln`.

## Точка входа в документацию

**Единый источник правды (SSOT)** находится в папке `docs/`. Начинай с:

1. **`docs/README.md`** — индекс всех документов, карта навигации, текущий статус
2. **`docs/invariants.md`** — жёсткие правила (I-01..I-13). **Загружай ВСЕГДА.**
3. **`docs/architecture/dependency-rule.md`** — куда класть код. **Загружай ВСЕГДА.**

Остальные документы загружай по контексту задачи (см. карту в `docs/README.md`).

## Критические правила (краткая выжимка)

- **I-01:** Revit API из WPF — только через `IExternalEventHandler`. Никаких прямых вызовов.
- **I-03:** Транзакции — только через `ITransactionService`. Запрещено `new Transaction(doc)`.
- **I-05:** Не хранить `Element`/`Connector` между транзакциями. Только `ElementId`.
- **I-09:** `SmartCon.Core` ссылается на RevitAPI.dll compile-time only. Разрешено использовать типы-carriers (ElementId, XYZ). Запрещено **вызывать** Revit API. Запрет `using System.Windows`.
- **I-10:** MVVM строго. `.xaml.cs` содержит только `DataContext = viewModel`.

## При создании/изменении кода

- Новые доменные классы → обнови `docs/domain/models.md`
- Новые интерфейсы → обнови `docs/domain/interfaces.md`
- Архитектурные решения → создай ADR в `docs/adr/`
- Смена статуса фазы → обнови `docs/roadmap.md` и `docs/README.md`

## Мульти-версионная сборка (ОБЯЗАТЕЛЬНО)

Проект поддерживает **8 версий Revit**: 2019, 2020, 2021, 2022, 2023, 2024 (net48) и 2025, 2026 (net8.0-windows).

### Паттерн группировки конфигураций (SSOT)

Каждая shipping-конфигурация покрывает **диапазон** версий Revit. Бинарник компилируется против
**минимальной API-версии** в диапазоне — это гарантирует обратную совместимость (Revit API additive-only).

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

### ЕДИНСТВЕННЫЙ правильный способ сборки — `build-and-deploy.bat`

Используй именованные конфигурации `Debug.R25/.R21/.R19` (НЕ `-p:RevitVersion=...`):

```bash
# Полный цикл: сборка + деплой всех версий
build-and-deploy.bat
```

Скрипт собирает 4 конфигурации (R25/R24/R21/R19) + updater и деплоит в Revit.
R25-бинарник копируется в папки Revit 2025 и 2026 (один бинарник для обеих версий).

### Почему НЕЛЬЗЯ использовать `-p:RevitVersion=...`

Пакет `Nice3point.Revit.Api.RevitAPI` использует `VersionOverride` зависящий от `$(RevitVersion)`.
При `dotnet build ... -p:RevitVersion=2025` restore НЕ видит `RevitVersion` из конфигурации
→ fallback на RevitAPI `2021.*` для net48 → API 2022+ недоступно → ошибки компиляции.

Именованные конфигурации (`Debug.R25`) парсят `RevitVersion` из имени в `Directory.Build.props`
**до** restore → каждая сборка получает правильную версию RevitAPI.

### Если нужно собрать только одну версию вручную

**ВАЖНО:** Собирай каждую конфигурацию ОТДЕЛЬНО. НЕ собирай solution — он подтянет лишние TFM.
Между конфигурациями разных TFM (net8 vs net48) может потребоваться `dotnet restore` —
RevitAPI NuGet-пакет имеет разные версии для разных TFM, и быстрое переключение между
конфигурациями без restore может дать ошибки компиляции.

```bash
# Revit 2025-2026 (net8.0-windows)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# Revit 2024 (net48)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24

# Revit 2021-2023 (net48)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21

# Revit 2019-2020 (net48)
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19
```

**Правила сборки:**
1. Собирай `SmartCon.App.csproj`, а НЕ `SmartCon.sln`
2. Каждую конфигурацию — отдельной командой
3. Если после сборки одной конфигурации другая падает с ошибками NuGet/restore — запусти `dotnet restore src/SmartCon.App/SmartCon.App.csproj` и повтори сборку

### Тесты

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

### Чеклист перед коммитом:
1. `build-and-deploy.bat` — 0 ошибок, 0 предупреждений на всех конфигурациях
2. Тесты — 0 падений
3. Инварианты I-01..I-13 не нарушены

## Инструменты поиска документации

В среде доступны три инструмента для поиска документации. Каждый покрывает свою область — не пытайся искать Revit API через REF, и наоборот.

### 1. Skill `revit-api` — Локальная база Revit API 2026

Расположен в `.agents/skills/revit-api/`. Содержит полную документацию по 2724 типам Revit API (v26.0.4.0) в виде предизвлечённых JSON-файлов.

**Внимание:** База основана на Revit 2026 (v26.0.4.0). Наш проект использует Revit 2025. Абсолютное большинство API идентично, но если обнаружишь расхождения — проверяй через `docs/references.md`.

**Как пользоваться (Python-скрипты из `.agents/skills/revit-api/scripts/`):**

```bash
# Поиск по ключевому слову (класс, метод, перечисление)
python .agents/skills/revit-api/scripts/search_api.py search "Connector"

# Члены класса (свойства и методы с описаниями)
python .agents/skills/revit-api/scripts/search_api.py class "Autodesk.Revit.DB.Connector"

# Полная документация класса (сигнатуры, Remarks, иерархия)
python .agents/skills/revit-api/scripts/extract_page.py --type "Autodesk.Revit.DB.Connector"

# Документация конкретного метода/свойства
python .agents/skills/revit-api/scripts/extract_page.py --id "M:Autodesk.Revit.DB.Connector.ConnectTo(Autodesk.Revit.DB.Connector)"
python .agents/skills/revit-api/scripts/extract_page.py --id "P:Autodesk.Revit.DB.Connector.Origin"

# Поиск по имени члена (какой класс содержит этот метод?)
python .agents/skills/revit-api/scripts/search_api.py member "GetParameters"

# Список всех пространств имён
python .agents/skills/revit-api/scripts/search_api.py namespaces

# Типы в конкретном пространстве имён
python .agents/skills/revit-api/scripts/search_api.py namespace "Autodesk.Revit.DB"
```

**Префиксы для `--id`:** `T:` = тип (класс/интерфейс/enum), `P:` = свойство, `M:` = метод, `E:` = событие, `Overload:` = все перегрузки метода.

**Быстрые паттерны:** Файл `.agents/skills/revit-api/references/core-patterns.md` содержит 11 готовых примеров кода (ExternalCommand, Transaction, FilteredElementCollector, Element creation и т.д.).

**Когда использовать:**
- Нужно узнать сигнатуру метода Revit API
- Нужно найти все свойства/методы класса
- Нужно понять как работает конкретный API (ConnectTo, Assimilate, GetParameters и т.д.)
- Нужно узнать в каком классе находится метод

### 2. MCP REF — Поиск по GitHub и Microsoft Learn

Инструменты: `ref_search_documentation(query)` + `ref_read_url(url)`.

**Что умеет:**
- Поиск по GitHub-репозиториям (README, docs, исходники)
- Поиск по Microsoft Learn документации (.NET, WPF, ASP.NET)
- Чтение найденных страниц с извлечением конкретных секций

**Для чего использовать:**
- Проверка актуальных версий NuGet-пакетов (xUnit, Moq, CommunityToolkit.Mvvm, MEDI)
- Уточнение общих .NET/WPF/MVVM паттернов и best practices
- Поиск примеров кода для стандартных библиотек (DI, сериализация, тестирование)

**Чего НЕ умеет:**
- Не ищет Revit API — для этого используй skill `revit-api`
- Не ищет узкоспециализированные алгоритмы — пиши код на основе общих принципов

**Как пользоваться:**
1. `ref_search_documentation(query)` — поиск. Запрос на английском с указанием языка/фреймворка (напр. `"CommunityToolkit.Mvvm NuGet latest version C#"`)
2. `ref_read_url(url)` — чтение. URL бери ТОЧНО из результата поиска (с хешем `#section`)

### 3. Skill `saury-revit` — Шаблон для новых Revit-проектов

**НЕ используется для текущего проекта.** Этот skill нужен только если пользователь просит создать **новый** Revit-плагин с нуля через `Saury.Revit.Template` dotnet-шаблон.

### Итого: что когда использовать

| Нужно | Инструмент |
|---|---|
| Сигнатура метода Revit API | `revit-api` skill → `search_api.py` / `extract_page.py` |
| Какой класс содержит метод | `revit-api` skill → `member` |
| Пример кода с Revit API | `revit-api` skill → `core-patterns.md` |
| Версия NuGet-пакета | MCP REF → `ref_search_documentation` |
| .NET/WPF/DI паттерн | MCP REF → `ref_search_documentation` |
| Создать новый Revit-проект | `saury-revit` skill |

## CI/CD — GitHub Actions

### Workflow-ы

| Файл | Триггер | Что делает |
|---|---|---|
| `build.yml` | push в main, PR, tags `v*` | Билдит 5 конфигураций (R19/R21/R24/R25/R26) + тесты |
| `codeql.yml` | push/PR в main, еженедельно | Security scanning (C#) |
| `stale.yml` | ежедневно | Закрывает issues/PR без активности 30+ дней |

### Branch Protection на main

Ветка `main` защищена:
- Обязательны 6 status checks: build (R19/R21/R24/R25/R26) + test
- Нужен 1 approval от CODEOWNERS
- Linear history (no merge commits)
- Admin может пушить напрямую (bypass)

### Dependabot — что ЗАПРЕЩЕНО обновлять

`dependabot.yml` уже игнорирует опасные пакеты. **Если агент меняет `Directory.Packages.props` — проверить:**
- `Microsoft.Extensions.DependencyInjection` — **ЗАМОРОЖЕН на 8.x** (9.0+ дропнул net48)
- `System.Text.Json` — **ЗАМОРОЖЕН на 8.x** (9.0+ может сломать net48)
- `Nice3point.Revit.Api.*` — версия определяется динамически из конфигурации

### Если CI упал

1. Прочитать лог: `gh run view <run_id> --log-failed`
2. 99% причин: `EnforceCodeStyleInBuild=true` + какой-то анализатор стал error
3. Решение: добавить severity override в `.editorconfig` (НЕ отключать `EnforceCodeStyleInBuild`)
4. НЕ трогать `Directory.Build.props` без крайней необходимости

### Release — как работает

1. **Локально:** `tools\release.bat` → `release.ps1` (инкремент версии, билд, тест, publish, ZIP, Inno Setup, git tag, push, GitHub Release)
2. **CI:** `build.yml` срабатывает на tag `v*` → валидирует сборку + тесты (не создаёт релиз)
3. **Единственный создатель релизов:** `release.ps1` — он же формирует changelog и загружает архивы

## Структура документации

```
docs/
├── README.md                          <- ТОЧКА ВХОДА (индекс)
├── invariants.md                      <- Жёсткие правила I-01..I-13
├── roadmap.md                         <- Фазы разработки
├── references.md                      <- Внешние ссылки на Revit API docs
├── architecture/
│   ├── solution-structure.md          <- Проекты, папки, файлы
│   ├── dependency-rule.md             <- Правило зависимостей слоёв
│   └── tech-stack.md                  <- Стек технологий
├── domain/
│   ├── models.md                      <- Все доменные модели с сигнатурами
│   ├── interfaces.md                  <- Все интерфейсы-контракты
│   └── glossary.md                    <- Единый словарь терминов
├── pipeconnect/
│   ├── state-machine.md               <- Диаграмма состояний PipeConnect
│   ├── algorithms.md                  <- Алгоритмы: выравнивание, параметры, фитинги
│   └── ui-spec.md                     <- Спецификация UI окон
├── plans/
│   └── improvement-plan.md            <- План улучшений
└── adr/
    ├── README.md                      <- Индекс ADR
    ├── 001-clean-architecture.md
    ├── 002-connector-description-type-code.md
    ├── 003-transaction-group-pattern.md
    ├── 004-json-mapping-storage.md
    ├── 005-formula-solver-ast.md
    ├── 006-external-event-pattern.md
    ├── 007-communitytoolkit-mvvm.md
    ├── 008-external-event-action-queue.md
    └── 009-vec3-for-core-math.md
```

## Структура исходного кода

```
src/
├── SmartCon.sln
├── SmartCon.Core/          <- Чистый C#. Модели, интерфейсы, алгоритмы
├── SmartCon.Revit/         <- Реализации интерфейсов Core через Revit API
├── SmartCon.UI/            <- Общая WPF-библиотека: стили, контролы
├── SmartCon.App/           <- Точка входа: IExternalApplication, Ribbon, DI
├── SmartCon.PipeConnect/   <- Модуль PipeConnect: Commands, ViewModels, Views
└── SmartCon.Tests/         <- Unit + ViewModel тесты (xUnit + Moq)
```
