---
trigger: always_on
---
Отвечай всегда на русском языке.


# SmartCon — Системная инструкция для AI-агентов

Ты работаешь над проектом **SmartCon** — плагином для Autodesk Revit 2025 (.NET 8 / C# 12 / WPF).
Флагманский модуль — **PipeConnect**: соединение трубных MEP-элементов в 3D двумя кликами.

## Точка входа в документацию

**Единый источник правды (SSOT)** находится в папке `docs/`. Начинай с:

1. **`docs/README.md`** — индекс всех документов, карта навигации, текущий статус
2. **`docs/invariants.md`** — жёсткие правила (I-01..I-10). **Загружай ВСЕГДА.**
3. **`docs/architecture/dependency-rule.md`** — куда класть код. **Загружай ВСЕГДА.**

Остальные документы загружай по контексту задачи (см. карту в README.md).

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

## MCP REF — использование для исследования

**Часто используй `ref_search_documentation` и `ref_read_url`** для:
- Проверки актуальных версий NuGet-пакетов (Microsoft.Extensions.DependencyInjection, xUnit, Moq)
- Уточнения API Revit — параметры методов, поведение классов
- Поиска примеров кода для специфических сценариев
- Решения технических нюансов при реализации

**Когда использовать:**
- Перед добавлением нового NuGet-пакета — проверь актуальную версию
- При работе с незнакомым классом Revit API — уточни сигнатуры
- При ошибках компиляции с Revit API — проверь изменения в версии 2025
- При реализации FormulaSolver — поищи лучшие практики AST-парсеров

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
└── adr/
    ├── README.md                      <- Индекс ADR
    ├── 001-clean-architecture.md
    ├── 002-connector-description-type-code.md
    ├── 003-transaction-group-pattern.md
    ├── 004-json-mapping-storage.md
    ├── 005-formula-solver-ast.md
    ├── 006-external-event-pattern.md
    ├── 007-communitytoolkit-mvvm.md
    └── 008-external-event-action-queue.md
