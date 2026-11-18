# ADR-076: Tool surface — intent-tools, generic data tools и запрет runtime C#

**Date:** 2026-11-18  
**Status:** accepted  
**Related:** ADR-075 (hybrid retrieval), [harness-ideal-plan §3.3](../agent/00-strategy/harness-ideal-plan.md), [H1 runbook](../agent/00-strategy/initial-improvement-runbook.md), [AGENT-FEEDBACK](../agent/AGENT-FEEDBACK.md)

## Context

Каталог сейчас — 27 инструментов; целевой профиль — 500–1000 операций и
100–200 навыков. Главный риск масштабирования — попытка обернуть каждый метод
Revit API в отдельный tool: thousands of JSON-схем не влезают в контекст,
деградируют выбор и превращают поддержку в квадратичную.

Исследование 2026 (Anthropic, Cloudflare, MCP, LangChain, RAG-MCP, Goose,
revit-mcp/pyRevit) даёт устойчивые выводы:

- 30–50 tools — практическая граница; при 51 tool LangChain ReAct падал с 43 %
  до 2 % на календарных задачах; Anthropic: 58 tools ≈ 55K токенов, tool search
  снижает стартовый контекст на 85 % и поднимает точность 49 % → 74 %;
- крупные API обслуживаются не «все tools в промпте», а композитными
  операциями, generic query/CRUD, progressive discovery и code execution
  по типизированному фасаду;
- code execution экономит токены (Anthropic: 150K → 2K в сценарии; Cloudflare:
  весь API как один `execute`), но добавляет sandbox, approvals и аудит;
- в Revit runtime-компиляция C# — тупик: net48 не выгружает сборки, Revit API
  нельзя изолировать в другом процессе, вызовы обязаны идти на главном потоке
  через `IExternalEventHandler`, а произвольный код с правами записи — не sandbox;
- Revit-MCP проекты подтверждают: 124–138 tools, `send_code_to_revit` без undo —
  prior art, а не цель; pyRevit Agent Runtime и bimwright идут к типизированному
  фасаду + `TransactionGroup`/`Assimilate`.

## Decision

### 1. Tool = пользовательское намерение, а не endpoint API

Новый tool добавляется, только если это:

- **композитная операция** («переименовать виды по эталону», «применить шаблон
  с проверкой семьи», «создать листы по Excel»), или
- **реальный примитив с инвариантами** (транзакция, undo, approval, read-only),
  или
- **meta-tool** (поиск, описание, план, состояние, отмена).

Запрещено создавать tools 1:1 по методам Revit API (`set_view_scale`,
`get_wall_comments`, `set_parameter_x`). Карточка tool обязана содержать
`Purpose`, `WhenToUse`, `WhenNotToUse`, `Examples`, `Synonyms`,
`NegativeExamples` — это acceptance-критерий H1-06.

### 2. Свойства и данные — generic-инструментами

Свойства — данные, не действия. Вместо per-property tools:

- `query_entities` — структурные фильтры, проекции, пагинация;
- `get_parameters(fields, filter)` / `get_element_details(fields)` — метаданные
  параметров берутся из Revit;
- `set_parameters` — batch, dry-run, diff, undo;
- `summarize_elements` — агрегаты и группировки.

Один generic-инструмент покрывает тысячи свойств, потому что схема динамическая.

### 3. Запрет runtime C# и raw code eval

В production-пути **не компилируем и не загружаем C# в процесс Revit**:

- net48 не умеет выгружать сборки — каждый скрипт висит до перезапуска;
- `AppDomain` не решает: Revit API-объекты не маршалятся, вызовы — только
  на главном потоке;
- collectible `AssemblyLoadContext` есть только на net8/net10 и всё равно
  протекает через статику/события;
- произвольный код с правами пользователя обходит I-01/I-03/I-05 и не даёт
  читаемого approval-диалога.

Запрещены `send_code_to_revit`, `execute_csharp`, raw Roslyn eval как
production-инструменты. Диагностический opt-in допускается только вне
пользовательских сценариев и с явным предупреждением.

### 4. Длинный хвост: план → интерпретатор → релизный C#

Порядок эскалации для нестандартной задачи:

1. **Декларативный план** (`execute_plan` и его развитие): шаги, зависимости,
   bulk-параметры, dry-run, read-back. Покрывает большинство BIM-операций.
2. **Песочница-интерпретатор** (H7): JS (Jint) или Lua (MoonSharp) поверх
   типизированного фасада `IRevitOps`. Скрипт — текст, не сборка; нет
   компиляции и загрузки assembly, нет утечки до перезапуска. Host-объекты —
   только allowlist фасада; без сети/ФС; timeout; отмена.
3. **C#-tool в релизе**: если операция нужна постоянно, она пишется как
   типизированный composite-tool, проходит тесты/eval и выходит в следующем
   релизе. Промоция скрипта в tool — только через review + eval, не в рантайме.

Фасад `IRevitOps` — единственная граница для скриптов: внутри
`ITransactionService`, `IExternalEventHandler`, `UniqueId`, версии, лимиты.
Тяжёлые циклы по элементам выполняются в C# внутри одного вызова фасада, а не
поэлементно в интерпретаторе.

### 5. Исполнение и параллельные сессии

- Один Revit-процесс = один API-поток; write-операции — только через
  `IExternalEventHandler`.
- Одна очередь на документ: `sessionId`, отмена, один активный write.
- Планирование/поиск/компиляция (если появится) — параллельно; исполнение —
  сериализовано.
- Один `TransactionGroup` на запуск плана/скрипта: успех → `Assimilate()` =
  один undo-entry; ошибка → `RollBack()`; частичных изменений нет.
- Подтверждение привязано к хешу плана/скрипта + документу; аудит — к OpId.

### 6. Публичная поверхность и внутренний каталог

- Резидентно: 10–15 meta-tools + 20–50 composite-tools.
- Внутренний каталог 500–1000 операций: карточки, паки, поиск (ADR-075),
  `load_tool_pack`/`enable_tools`; схемы не отправляются, пока не загружены.
- Retrieval — BM25 сейчас, hybrid vector — H6; decision-модель — reranker на
  100–200 кандидатах, не единственный prefilter.

## Consequences

- H1-06 получает явный критерий: карточка tool описывает намерение, не API;
  никаких 1:1 обёрток.
- H1 не добавляет code execution, runtime C# и per-property tools.
- H7 (typed facade + sandbox interpreter) появляется в roadmap после H6/H1;
  до него длинный хвост закрывается планом и релизными composite-tools.
- Любой новый tool проходит review-гейт: intent, инварианты, dry-run, undo,
  eval; иначе отклоняется.
- Оценка масштаба: 500–1000 карточек — это внутренний каталог, а не 500–1000
  tools в промпте.