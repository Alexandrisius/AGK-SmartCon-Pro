Отвечай всегда на русском языке.

# SmartCon — Системная инструкция для AI-агентов

Ты работаешь над проектом **SmartCon** — плагином для Autodesk Revit 2025 (.NET 8 / C# 12 / WPF).
Флагманский модуль — **PipeConnect**: соединение трубных MEP-элементов в 3D двумя кликами.

Исходный код расположен в `src/`. Solution: `src/SmartCon.sln`.

## Точка входа в документацию

**Единый источник правды (SSOT)** находится в папке `docs/`. Начинай с:

1. **`docs/README.md`** — индекс всех документов, карта навигации, текущий статус
2. **`docs/invariants.md`** — жёсткие правила (I-01..I-10). **Загружай ВСЕГДА.**
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

Проект поддерживает **6 версий Revit**: 2019, 2021, 2022, 2023, 2024 (net48) и 2025 (net8.0-windows).

**Правило:** После каждого значимого изменения кода — собери **ВСЕ версии последовательно** (не параллельно — конфликт за obj/ файлы):

```bash
# Последовательная сборка всех версий (обязательно!)
dotnet build src/SmartCon.sln -p:RevitVersion=2025 && dotnet build src/SmartCon.sln -p:RevitVersion=2024 && dotnet build src/SmartCon.sln -p:RevitVersion=2023 && dotnet build src/SmartCon.sln -p:RevitVersion=2022 && dotnet build src/SmartCon.sln -p:RevitVersion=2021 && dotnet build src/SmartCon.sln -p:RevitVersion=2019
```

**Также запусти тесты:**
```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -p:RevitVersion=2025
```

**Чеклист перед коммитом:**
1. Сборка 6 версий Revit — 0 ошибок, 0 предупреждений
2. Тесты — 0 падений
3. Инварианты I-01..I-10 не нарушены

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

## Структура документации

```
docs/
├── README.md                          <- ТОЧКА ВХОДА (индекс)
├── invariants.md                      <- Жёсткие правила I-01..I-10
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
