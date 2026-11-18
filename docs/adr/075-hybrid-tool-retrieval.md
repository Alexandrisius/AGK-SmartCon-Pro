# ADR-075: Гибридный retrieval каталога тулов и навыков (BM25 ∪ vector → decision reranker)

**Date:** 2026-11-18  
**Status:** accepted  
**Related:** ADR-006 (ExternalEvent), [benchmark decision-моделей](../agent/00-strategy/decisions-benchmark.md), [harness-ideal-plan §3.3](../agent/00-strategy/harness-ideal-plan.md), H1-08/H1-11/H1-12 в [initial-improvement-runbook](../agent/00-strategy/initial-improvement-runbook.md)

## Context

Сейчас каталог — 27 инструментов; маршрутизация — BM25-shortlist (`RuleToolRouter`),
опциональная decision-модель (`DecisionsToolRouter`) и `find_tools`. Этого достаточно:
на 27 тулах BM25 попадает в топ, а decision-модель не даёт измеримого выигрыша.

Целевой масштаб — **500–1000 инструментов и 100–200 навыков** (BIM-ops, см.
`harness-ideal-plan.md` §3.3). На этом масштабе:

- BM25 сам по себе не покрывает семантические формулировки («приведи в порядок»,
  «как эталон», «сводный») и деградирует на синонимах;
- decision-модель хорошо работает на **100–200 кандидатах** (бенчмарк на 22–27
  инструментах — потолок 100 %/100 %, но это не экстраполируется на 1000);
- единственный prefilter (BM25 top-N) может **отрезать семантически близкий
  инструмент**, в тексте которого нет ключевых слов запроса — подтверждённый
  опыт владельца на другом поисковом продукте;
- передавать 1000 схем в промпт нельзя (≈1.5M токенов), а 1000 карточек в
  decision-модель — дорого и ненадёжно («lost in the middle»).

Решение владельца (2026-11-18): строить **двухретриверный конвейер с
объединением кандидатов и decision-моделью как финальным reranker'ом**, а не
один prefilter. В окне настроек появится выбор embedding-модели и reranker'а.

## Decision

### 1. Два независимых ретривера, объединение — не пересечение

- **Лексический:** BM25 по карточке (name + purpose + when + examples + synonyms
  + negative examples).
- **Семантический:** vector search по эмбеддингам карточки.
- Кандидаты **объединяются** (union) и сливаются через RRF:
  `score = Σ 1/(k + rank)`, `k = 60`; дедупликация по имени инструмента.
- Ни один ретривер не является единственным входом reranker'а; top-K берётся
  **от каждого** (K ≤ 50), а не «top-N BM25, который отрезал остальное».

### 2. Decision-модель — reranker, не ретривер

- Вход: 100–200 объединённых кандидатов (имя + короткое описание ≤240 символов,
  risk, pack). Выход: choice/ranking; его порядок — основной.
- Core-инструменты (`get_project_overview`, `list_views`, `list_levels`,
  `query_elements`, `find_tools`, `execute_plan`, `report_tooling_feedback`,
  `rename_views`, `apply_view_template`) закреплены и не вытесняются.
- При недоступности/ошибке decision-модели — порядок union, затем BM25
  (fail-closed, без падения хода).
- Risk gate остаётся отдельным Noul-вопросом к той же модели.

### 3. Масштабные пороги

| Размер каталога | Схема |
|---|---|
| ≤100 | BM25 + decision напрямую по каталогу (текущее поведение) |
| 100–200 | BM25 ∪ vector → decision reranker; decision-over-all ещё допустим |
| 200–500 | обязателен vector-ретривер; decision только по объединённым 100–200 |
| 500–1000 + 200 навыков | два уровня: retrieval по метаданным → decision reranker → `load_tool_pack`/`load_skill`; тела схем/навыков не в промпте до загрузки |

Порог переключения — не число, а метрика: если BM25 top-14 recall на routing-eval
падает ниже 95 %, vector-ветка включается принудительно.

### 4. Эмбеддинги каталога — предвычисленные

Карточки тулов/навыков меняются только с релизом, поэтому:

- векторы карточек строятся **офлайн** (dev/build) и поставляются с плагином
  (JSON + бинарные float32), версионируются хешем каталога;
- в рантайме вычисляется только **эмбеддинг запроса** (локальная модель или
  API — выбирается в настройках);
- индекс в памяти: 1000 × 384 float ≈ 1.5 МБ; пересборка при смене хеша каталога.

### 5. Настройки (обязательное требование)

В окне настроек агента добавить секцию **Retrieval**:

- **Embedding-модель:** провайдер (local ONNX / OpenRouter / OpenAI-compatible),
  имя модели, endpoint, ключ; кнопка «Проверить» (размерность, latency);
- **Reranker:** decision-модель (существующий выбор `Не использовать` /
  `pplx-decider-v1.3:free` / `pplx-decider-latest` / `jev-1.13.0`) + отдельный
  provider/endpoint/key, если reranker не совпадает с decision-моделью;
- **Параметры:** `retrieval.mode = bm25 | hybrid | decisions`, `topK` на ретривер,
  `candidatePool` (100–200), `rrfK`, порог включения;
- значения сохраняются в `secrets.env`/`agent.settings.json`, применяются без
  перезапуска; при выключенном retrieval поведение — текущий BM25.

### 6. Границы и зависимости

- Контракты `IToolRetriever`, `IEmbeddingProvider`, `IReranker` — в
  `SmartCon.Core` (без Revit/WPF); реализации BM25/vector/decision — в
  `SmartCon.Agent`.
- `SmartCon.Core` не делает сетевых вызовов; эмбеддинги и decision-модель —
  только в Agent.
- Векторный RAG **по содержимому Revit-модели** остаётся non-goal: для модели —
  structural query + FTS5. Эмбеддинги — только для каталога тулов/навыков.

## Metrics / acceptance

- Routing eval ≥200 запросов (100 реальных BIM-фраз + 100 синтетических), каталог
  1000 карточек: top-1 ≥80 %, top-5 recall ≥95 %, false tool rate <3 %.
- Отдельно измеряются recall@K **каждого** ретривера и union; кейсы «BM25
  промахнулся, union нашёл» — обязательная часть отчёта.
- p95: BM25 <100 ms, query embedding <150 ms (local) / <500 ms (API),
  reranker <2 s. Стоимость и токены routing-вызова — в `token_usage`.
- A/B на одних и тех же запросах: `rules`, `hybrid` (без reranker),
  `hybrid + decisions`; победитель определяется task success, а не только top-1.
- 1000-карточный тест: промпт с паками ≤8k токенов; схема грузится только после
  `load_tool_pack`; смена набора инвалидирует cache осознанно.

## Implementation phase

Решение **не входит в H1**. H1 (tool catalog v2, паки, `find/load`, freeze,
context budgeting) остаётся в текущем объёме и проверяется на BM25 + опциональном
decision reranker. Векторный конвейер — отдельный эпик **H6, последний**:
после H1, H1b (model entity search, ADR-077), H2–H5 и H7; триггер — routing-eval
на 1000 карточках даёт BM25 top-5 recall <95 %, либо каталог превысил ~200
видимых инструментов.
- H6 включает `Bm25ToolRetriever`, `VectorToolRetriever`, `RrfFusion`,
  `DecisionToolReranker`, предвычисленный индекс карточек и UI выбора
  embedding-модели/reranker'а;
- до H6 настройки embedding/reranker не добавляются: это обязало бы тащить
  индекс и провайдера раньше, чем измерена необходимость.

## Consequences

- Текущие 27 тулов продолжают работать на BM25; decision-модель остаётся
  выключенной по умолчанию до масштабирования каталога.
- `find_tools` получает режим листинга `fields=["name","description"]` без схем —
  каталог можно просмотреть целиком, не угадывая формулировку (это можно сделать
  уже в H1, без vector).
- В H6 появляется зависимость от embedding-провайдера в рантайме; при его
  недоступности hybrid деградирует до BM25 без ошибки хода.
- H1-08/H1-11/H1-12 не расширяются vector-задачами; H1-11 фиксирует замер
  BM25/decision и решение «нужен ли H6».