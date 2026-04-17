# Architecture Decision Records (ADR)

> Индекс архитектурных решений проекта SmartCon.
> Загружать: при вопросах «почему так сделано?»

## Формат

Каждый ADR — отдельный файл с номером. Статус: `accepted`, `superseded`, `deprecated`.

## Индекс

| # | Решение | Статус | Дата |
|---|---------|--------|------|
| [001](001-clean-architecture.md) | Clean Architecture: Core без зависимостей от Revit/WPF | accepted | 2026-03-25 |
| [002](002-connector-description-type-code.md) | Хранение типа соединения в Connector.Description | accepted | 2026-03-25 |
| [003](003-transaction-group-pattern.md) | TransactionGroup + Assimilate для PipeConnect | accepted | 2026-03-25 |
| [004](004-json-mapping-storage.md) | JSON в AppData для хранения маппинга фитингов | accepted | 2026-03-25 |
| [005](005-formula-solver-ast.md) | AST-парсер для формул Revit (универсальный модуль) | accepted | 2026-03-25 |
| [006](006-external-event-pattern.md) | IExternalEventHandler для WPF -> Revit API | accepted | 2026-03-25 |
| [007](007-communitytoolkit-mvvm.md) | CommunityToolkit.Mvvm вместо ручной MVVM-инфраструктуры | accepted | 2026-03-25 |
| [008](008-external-event-action-queue.md) | Action Queue паттерн для ExternalEvent dispatch | accepted | 2026-03-25 |
| [009](009-vec3-for-core-math.md) | Vec3 вместо XYZ для чистой математики в Core | accepted | 2026-03-26 |
| [010](010-fitting-chain-resolver.md) | FittingChainResolver — единая система подбора цепочек фитингов | accepted | 2026-04-17 |
| [011](011-dn-symbol-name-in-dropdown.md) | Отображение имени типоразмера в выпадающем списке DN | proposed | 2026-04-17 |
