Отвечай всегда на русском языке.

# SmartCon — Системная инструкция для AI-агентов

Ты работаешь над проектом **SmartCon** — плагином для Autodesk Revit 2025 (.NET 8 / C# 12 / WPF).
Флагманский модуль — **PipeConnect**: соединение трубных MEP-элементов в 3D двумя кликами.

Исходный код расположен в `src/`. Solution: `src/SmartCon.sln`.

## Обязательная подготовка перед ЛЮБОЙ задачей

**Перед началом работы агент ОБЯЗАН:**

1. **Прочитать обязательные файлы** (в этом порядке):
   - `docs/README.md` — понять контекст проекта
   - `docs/invariants.md` — выучить жёсткие правила I-01..I-17
   - `docs/architecture/dependency-rule.md` — понять куда класть код
   - `docs/architecture/solution-structure.md` — понять структуру проектов

2. **Провести аудит решения через Exa**:
   - Задай 2-3 запроса в `get_code_context_exa` или `exa_web_search_exa` 
   - Убедись, что ты мыслишь правильно в рамках проекта
   - Проверь best practices для используемых технологий
   - Если `revit-api` skill не даёт результата — обязательно поищи в Exa

3. **Проанализировать код** через субагентов:
   - Запусти Task-агента для анализа нужного участка кода
   - Делегируй изучение связанных модулей субагентам
   - Не анализируй большие файлы в основном контексте

**ЗАПРЕЩЕНО начинать работу без выполнения пунктов 1-3.**

## Архитектура работы агентов

### Главный агент = Оркестратор

Главный агент **НЕ ПИШЕТ** основной объём кода. Его задачи:
- Знать всю документацию проекта идеально
- Использовать поиск (Exa, revit-api skill) для сбора контекста
- Делегировать работу субагентам через `Task` tool
- Проверять результаты субагентов
- Исправлять мелкие баги
- Принимать архитектурные решения

**Главный агент пишет код только для:**
- Мелких правок (1-5 строк)
- Исправления очевидных багов
- Изменений в конфигурационных файлах

### Субагенты = Исполнители

Субагенты (Task tool) выполняют основную работу:
- Анализ кода и документации
- Написание новых модулей
- Рефакторинг
- Исследование через Exa
- Изучение конкретных файлов

**Правила использования субагентов:**
- Запускай субагентов параллельно для независимых задач
- Каждый субагент — одна конкретная задача
- Субагент получает полный контекст в prompt
- Главный агент анализирует результаты и интегрирует

## Коммуникация с пользователем

### Вопросы — ТОЛЬКО через tools

**ЗАПРЕЩЕНО** задавать вопросы пользователю текстом в ответе.

**ОБЯЗАТЕЛЬНО** использовать `question` tool для:
- Уточнения требований
- Выбора между вариантами реализации
- Запроса недостающей информации
- Подтверждения архитектурных решений

**Пример правильного поведения:**
```
Пользователь: "Добавь функцию X"
→ Агент: [читает invariants, архитектуру]
→ Агент: [запускает субагентов для анализа]
→ Агент: [использует question tool] 
  "Нужно уточнение: функцию X добавить в модуль A или модуль B?"
→ Пользователь: "В модуль A"
→ Агент: [продолжает работу]
```

## Точка входа в документацию

**Единый источник правды (SSOT)** находится в папке `docs/`. Начинай с:

1. **`docs/README.md`** — индекс всех документов, карта навигации, текущий статус
2. **`docs/invariants.md`** — жёсткие правила (I-01..I-17). **Загружай ВСЕГДА.**
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
- Смена статуса фазы → обнови `docs/README.md`

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
Между конфигурациями разных TFM (net8 vs net48) **ОБЯЗАТЕЛЬНО** делай `dotnet restore` —
RevitAPI NuGet-пакет имеет разные версии для разных TFM, и без restore будут ложные ошибки
компиляции (CS0618, CS0234 и т.д.) из-за того, что в кэше осталась предыдущая версия API.

**Правильный порядок сборки при ручной проверке:**

```bash
# 1. Сначала net8.0-windows (Revit 2025-2026)
dotnet restore src/SmartCon.App/SmartCon.App.csproj
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# 2. Затем net48 (Revit 2024) — ОБЯЗАТЕЛЬНО restore!
dotnet restore src/SmartCon.App/SmartCon.App.csproj
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24

# 3. Затем net48 (Revit 2021-2023) — restore не нужен, тот же TFM
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21

# 4. Затем net48 (Revit 2019-2020) — restore не нужен, тот же TFM
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19
```

**Правила сборки:**
1. Собирай `SmartCon.App.csproj`, а НЕ `SmartCon.sln`
2. Каждую конфигурацию — отдельной командой
3. **При переходе между разными TFM (net8 → net48 или net48 → net8) — ВСЕГДА делай `dotnet restore` перед сборкой**

### Тесты

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

### Чеклист перед коммитом:
1. `build-and-deploy.bat` — 0 ошибок, 0 предупреждений на всех конфигурациях
2. Тесты — 0 падений
3. Инварианты I-01..I-17 не нарушены

## Инструменты поиска документации

В среде доступны **три инструмента** для поиска информации. Каждый покрывает свою область — не пытайся искать Revit API через REF, и наоборот.

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

### 2. MCP REF — ТОЛЬКО NuGet, .NET, Microsoft Learn

**ЗАПРЕЩЕНО использовать REF для общего веб-поиска. REF — это НЕ поисковик.**

Инструменты: `ref_search_documentation(query)` + `ref_read_url(url)`.

**Строго ограниченный scope (только эти кейсы):**
- Проверка актуальных версий NuGet-пакетов (xUnit, Moq, CommunityToolkit.Mvvm, MEDI)
- Примеры кода для .NET-библиотек из Microsoft Learn (DI, сериализация, тестирование)
- Чтение конкретных секций по URL с якорем `#section`

**ЗАПРЕТЫ:**
- **НЕ** использовать для поиска в интернете — используй Exa
- **НЕ** использовать для Revit API — используй skill `revit-api`
- **НЕ** использовать для StackOverflow, форумов, блогов — используй Exa
- **НЕ** использовать если нужен "пример кода" без указания конкретной библиотеки — используй Exa

**Как пользоваться:**
1. `ref_search_documentation(query)` — поиск. Запрос на английском с указанием языка/фреймворка (напр. `"CommunityToolkit.Mvvm NuGet latest version C#"`)
2. `ref_read_url(url)` — чтение. URL бери ТОЧНО из результата поиска (с хешем `#section`)

### 3. MCP Exa — ЕДИНСТВЕННЫЙ инструмент для веб-поиска

**Если нужно что-то найти в интернете — это всегда Exa. Никаких исключений.**

Инструменты: `exa_web_search_exa(query)` + `exa_web_fetch_exa(urls)`.

**Что умеет:**
- Поиск по всему интернету: форумы, блоги, GitHub, StackOverflow, техническая документация
- Загрузка и очистка контента страниц (markdown без рекламы)
- Оптимизированные highlights (выдержки) из результатов

**Для чего использовать (все кейсы веб-поиска):**
- Примеры кода Revit API с форумов Autodesk и блогов
- Общие алгоритмы, паттерны программирования
- Примеры использования библиотек из StackOverflow/GitHub
- Любые вопросы, требующие поиска в интернете

**Чего НЕ умеет:**
- Не заменяет `revit-api` skill для точных сигнатур — используй для примеров и обсуждений
- Не ищет по Microsoft Learn/NuGet так же точно как REF (но покрывает 90% кейсов)

**Важное правило:** Если `revit-api` skill не даёт результата (например, метод не найден или информация устарела) — **обязательно** ищи в Exa. Вся документация Revit API доступна в интернете, и Exa найдёт актуальные примеры и обсуждения.

**Как пользоваться:**
1. `exa_web_search_exa(query)` — поиск. Запрос на английском (напр. `"Revit API Connector ConnectTo C# example"`)
2. `exa_web_fetch_exa(urls)` — чтение. URL из результатов поиска Exa

### 3a. `get_code_context_exa` — Специализированный поиск кода (внутри Exa)

**Доступен только если в MCP-конфиг добавлен `?tools=get_code_context_exa`:**
```json
{
  "exa": {
    "type": "remote",
    "url": "https://mcp.exa.ai/mcp?tools=get_code_context_exa",
    "enabled": true
  }
}
```

**Чем отличается от `exa_web_search_exa`:**
- Ищет по GitHub, StackOverflow, технической документации
- Возвращает **готовые snippets** (не ссылки)
- Параметр `tokensNum` контролирует объём ответа (1000–50000, default ~5000)
- Оптимизирован для агентов: минимум шума, максимум кода

**Когда использовать:**
- Нужен быстрый ответ с кодом (без необходимости переходить по ссылкам)
- Поиск конкретных паттернов/примеров ("how to use DependencyInjection in WPF")
- Нужно много кода сразу (укажи `tokensNum: 15000`)

**Pipeline внутри Exa:**
1. Быстрый поиск кода → `get_code_context_exa` (если доступен)
2. Общий поиск / чтение страниц → `exa_web_search_exa` + `exa_web_fetch_exa`

**Повторение критически важного правила: REF — НЕ поисковик. Для любого поиска в интернете используй Exa.**

### Pipeline поиска (критически важно)

**Правило: не используй REF как универсальный поиск. REF — только для NuGet/.NET docs. Для всего остального — Exa.**

**Правило: Перед началом любой задачи — проведи аудит через Exa.**
- Задай 2-3 запроса о best practices для решаемой задачи
- Проверь, что твой подход соответствует актуальным паттернам
- Если `revit-api` skill не даёт результата — ищи в Exa (вся Revit API документация доступна в интернете)
- Не начинай писать код до проверки подхода через поиск

| Нужно | Первый выбор | Если не помогло |
|---|---|---|
| Сигнатура метода Revit API | `revit-api` skill → `search_api.py` / `extract_page.py` | **Exa** → `exa_web_search_exa` |
| Какой класс содержит метод | `revit-api` skill → `member` | **Exa** → `exa_web_search_exa` |
| Пример кода с Revit API (форумы, блоги) | **Exa** → `get_code_context_exa` | `exa_web_search_exa` |
| Версия NuGet-пакета | **REF** → `ref_search_documentation` | — |
| .NET/WPF/DI паттерн | **REF** → `ref_search_documentation` | Exa |
| Общие алгоритмы, примеры кода | **Exa** → `get_code_context_exa` | `exa_web_search_exa` |
| StackOverflow/GitHub примеры | **Exa** → `get_code_context_exa` | `exa_web_search_exa` |
| Поиск в интернете (любой) | **Exa** → `exa_web_search_exa` | — |
| Форумы, блоги, статьи | **Exa** → `exa_web_search_exa` | — |

## CI/CD — GitHub Actions

### Workflow-ы

| Файл | Триггер | Что делает |
|---|---|---|
| `build.yml` | push в main, PR, tags `v*` | Smart CI: если `src/**` не менялся — skip за 30 сек, иначе полная сборка 5 конфигураций + тесты |
| `codeql.yml` | push в main, еженедельно | Security scanning (C#). НЕ запускается на PR — информационный, не блокирует merge |
| `stale.yml` | ежедневно | Закрывает issues/PR без активности 30+ дней |

### Branch Protection на main

Ветка `main` защищена:
- Обязательны 2 umbrella-checks: `build-success` + `test-success` (всегда завершаются, даже если src не менялся)
- Review **не требуется** (solo-мейнтейнер не может одобрить свой PR — ограничение GitHub)
- `CODEOWNERS` файл существует для документации владения кодом и автоматических review-реквестов контрибьюторам
- Linear history (no merge commits) — только squash-merge
- Force push запрещён
- `enforce_admins: false` — владелец может bypass при необходимости
- **ВСЕГДА через PR** — по конвенции (AGENTS.md), даже если GitHub технически позволяет push напрямую
- При появлении контрибьюторов — пересмотреть review policy

### Workflow слияния feature → main (ОБЯЗАТЕЛЬНО)

**Единственный допустимый путь: feature branch → PR → squash-merge через GitHub UI.**

**Если агент находится на `main` и пользователь просит изменить код:**
1. НЕ менять файлы на main
2. Создать feature-ветку: `git checkout -b feature/название`
3. Менять код, коммитить, создавать PR — всё на feature-ветке

**КРИТИЧЕСКОЕ ПРАВИЛО: НЕ создавать PR без явного запроса пользователя**

Если пользователь просит «коммит», «сохранить», «запушить» — агент делает только `git add` → `git commit` → `git push` в **текущую ветку**. Никаких PR, squash-merge, удаления веток и перехода на main — если пользователь НЕ просил «вмёржить в main».

Когда пользователь просит «закоммитить и вмёржить в main», агент **обязан**:

1. **Коммит** в feature-ветку (обычный commit, не amend)
2. **Push** feature-ветки в remote: `git push -u origin <branch>`
3. **Создать PR** через `gh pr create`:
   ```bash
   gh pr create --title "feat: описание" --base main --head <branch>
   ```
4. **Сообщить пользователю** ссылку на PR
5. **Дождаться** зелёных CI-чеков (`build-success` + `test-success`)
6. **Squash-merge** через `gh pr merge --squash` (НЕ merge, НЕ rebase)
7. **Удалить локальную feature-ветку**: `git checkout main && git pull && git branch -d <branch>`
   (remote-ветка удаляется автоматически GitHub — `delete_branch_on_merge: true`)

**ЗАПРЕЩЕНО:**
- `git checkout main && git merge ...` — прямой merge в main
- `git push --force origin main` — force push на main (только при ликвидации мусора через API)
- `git config core.autocrlf ...` — менять глобальные настройки git
- Обход Branch Protection через `gh api` — только при крайней необходимости (ликвидация мусорных коммитов), с обязательным восстановлением

**Если CI упал в PR:**
- Не мёржить. Исправить на feature-ветке, push, ждать повторного CI.

**Формат заголовка PR:** `feat: ...` | `fix: ...` | `chore: ...` | `docs: ...` (Conventional Commits)

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
├── invariants.md                      <- Жёсткие правила I-01..I-17
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
├── SmartCon.Core/              <- Чистый C#. Модели, интерфейсы, алгоритмы
├── SmartCon.Revit/             <- Реализации интерфейсов Core через Revit API
├── SmartCon.UI/                <- Общая WPF-библиотека: стили, контролы
├── SmartCon.App/               <- Точка входа: IExternalApplication, Ribbon, DI
├── SmartCon.PipeConnect/       <- Модуль PipeConnect: Commands, ViewModels, Views
├── SmartCon.ProjectManagement/ <- Модуль Share Project: Commands, ViewModels, Views
├── SmartCon.FamilyManager/     <- Модуль FamilyManager: dockable panel, SQLite catalog
├── SmartCon.Updater/           <- Standalone .NET 8 updater
└── SmartCon.Tests/             <- Unit + ViewModel тесты (xUnit + Moq)
```
