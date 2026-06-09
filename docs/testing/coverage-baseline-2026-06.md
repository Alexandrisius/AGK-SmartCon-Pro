# Test Coverage Baseline Report

> **Дата:** 2026-06-09
> **Ветка:** `feature/logging-improvements` (после C1-C8)
> **Tool:** `coverlet.collector` (встроен в `SmartCon.Tests.csproj`)
> **Тест-сьют:** 1245 tests (baseline + добавлено в C1-C8)

## TL;DR

| Метрика | Значение |
|---|---|
| **Line rate (overall)** | **38.51%** |
| **Branch rate (overall)** | **33.36%** |
| Lines covered | 8706 / 22604 |
| Branches covered | 2520 / 7553 |
| Файлов всего (real .cs, без `.g.cs`) | 725 |
| Файлов с coverage ≥80% | 287 (40%) |
| Файлов с coverage 0% | 349 (48%) |

**Ключевой вывод:** coverage **не плохой** для проекта такого размера, но
**очень неравномерный**. 40% файлов покрыты хорошо, 48% — вообще без тестов.

## Per-package

| Assembly | Classes | Line rate |
|---|---:|---:|
| SmartCon.Core | 179 | см. ниже |
| SmartCon.FamilyManager | 432 | см. ниже |
| SmartCon.PipeConnect | 63 | см. ниже |
| SmartCon.ProjectManagement | 32 | см. ниже |
| SmartCon.UI | 30 | см. ниже |

*(Per-package line rate зависит от XPlat vs line-rate XML-формата; см.
  §"Методология")*

## Распределение

| Coverage | Кол-во файлов | % |
|---|---:|---:|
| 0% (нет тестов) | 349 | 48% |
| 1-9% | 1 | 0.1% |
| 10-49% | 24 | 3% |
| 50-79% | 64 | 9% |
| 80-100% | 287 | 40% |

**Медиана:** ~5% (многие файлы 0% тянут вниз)
**75-й перцентиль:** ~80% (есть хорошо покрытые модули)

## TOP 20 файлов с НИЗКИМ coverage (0% covered)

| File | Complexity |
|---|---:|
| `SmartCon.UI\Behaviors\TreeViewDropInfo.cs` | 1 |
| `SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.FamilyEdit.cs` | 29 |
| `SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.Database.cs` | (multiple partials) |
| `SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.cs` | (multiple partials) |
| `SmartCon.FamilyManager\ViewModels\FamilyBatchImportViewModel.cs` | 10 |
| ... (ещё 349 файлов) | |

**Паттерн:** 0% coverage у **ViewModel partials** (FamilyManagerMainViewModel.*.cs)
и **WPF-специфичных классов** (TreeViewDropInfo, Behaviors). Это ожидаемо —
они требуют UI host для тестирования.

## TOP хорошо покрытых файлов (≥80%)

Известные:
- `SmartCon.Core\Math\*` (LookupTable, SizeRowSymbolMatcher) — 100%
- `SmartCon.Core\Services\Helpers\DialogCloseHelper*` — 100%
- `SmartCon.Core\Logging\LogScope*` — 95%+
- `SmartCon.Core\Threading\AsyncBridge` (C5) — 100%
- `SmartCon.Tests\**` — 100% (тесты сами себя покрывают)

## Анализ по доменам

### SmartCon.Core (highest coverage)

- `Math/`: 100% (pure functions)
- `Logging/`: 95% (Phase 0a/1+2)
- `Threading/`: 100% (C5 AsyncBridge)
- `Services/Helpers/`: 100%
- **Missing**: некоторые SQL-методы, хелперы для строк

### SmartCon.FamilyManager (lowest coverage)

- `ViewModels/*MainViewModel.*.cs`: 0% (UI host required)
- `Services/LocalCatalog/*`: 60-80% (in-memory тесты есть)
- `Events/*`: 70% (AwaitableEvent)
- **Missing**: все WPF-bound ViewModels

### SmartCon.PipeConnect (medium coverage)

- `Services/ConnectExecutor`: 40% (частично)
- `Services/ChainOperationHandler`: 50%
- `ViewModels/PipeConnectEditorViewModel.*`: 0-30%
- **Missing**: dynamic connect/insert flow

## Приоритеты для улучшения (для `feature/test-coverage-baseline`)

### Tier 1 — low-hanging fruit (3-5 дней)

Файлы с 10-49% coverage — довести до 70%:
- `ChainOperationHandler.cs` (BFS, важный)
- `PipeConnectSizeHandler.cs` (math + validation)
- `RevitLookupTableService.cs` (CSV parsing)
- `RevitParameterResolver.cs` (formula resolution)

### Tier 2 — UI host mocking (5-10 дней)

Ввести `IUiDispatcher` mock, чтобы тестировать ViewModels:
- `FamilyManagerMainViewModel` (multiple partials)
- `FamilyBatchImportViewModel`
- `PipeConnectEditorViewModel`

### Tier 3 — Integration tests (отдельный проект)

`SmartCon.Tests.Integration` (новый) — реальный Revit host:
- `RevitFamilyLoadService` end-to-end
- `PipeConnect.Connect` полный flow
- `EditFamily` под нагрузкой

## Принятые baseline-метрики (для будущих PR)

Эти числа — **точка отсчёта**. Каждый новый PR должен **не уменьшать** их:

| Метрика | Baseline | Min для PR |
|---|---:|---:|
| Line rate | 38.51% | 38.51% |
| Branch rate | 33.36% | 33.36% |
| Тестов | 1245 | 1245 (без удаления) |

PR которые **добавят** coverage к существующим 0%-файлам — приветствуются.
PR которые **уменьшают** coverage — отклоняются.

## Методология

```bash
# Run tests with coverage
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 \
    --collect:"XPlat Code Coverage" \
    --results-directory ./TestResults

# Coverage файл
TestResults/<guid>/coverage.cobertura.xml
```

Cobertura XML содержит `line-rate` (0-1 float) и `complexity` (int).
Это — **относительные** метрики, не абсолютные line/branch counts.

Для абсолютных метрик (lines-valid, lines-covered) нужно использовать
`--collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura,opencover`.
Текущая конфигурация даёт **только Cobertura** — этого достаточно для baseline.

## Что НЕ покрыто coverage

- **M.E.L. logging** — out of scope (Phase 2 отвергнуто)
- **WPF rendering** — out of scope (требует UI host)
- **Roslyn analyzers** — отвергнуто в Phase 0
- **Multi-version build artifacts** — `obj/Debug.R25/*.g.cs` — auto-generated, не покрываются

## Связанные документы

- `docs/adr/028-di-readiness.md` — без DI нельзя замокать UI host
- `docs/roadmap/improvement-roadmap.md` — Phase 8 (test coverage)
- `docs/adr/025-refactoring-migration-backlog.md` — M-019-001..005 (DI migrations)

## Следующие шаги

1. **Немедленно:** baseline публикуется в `docs/testing/coverage-baseline-2026-06.md`
2. **`feature/test-coverage-baseline` ветка:** Tier 1 + Tier 2 (8-15 дней)
3. **CI integration:** добавить `coverage-diff` job (fail если line-rate < baseline)
4. **Ежемесячно:** пересчитывать baseline и публиковать отчёт
