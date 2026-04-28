# FamilyManager Unified Documentation Pack

## Назначение

Этот пакет объединяет стратегические и MVP-документы FamilyManager в единую систему для разработки модуля BIM content management внутри smartCon.

Пакет рассчитан на поэтапную разработку: сначала стратегия и стек, затем продуктовая рамка MVP, доменная модель, схема данных, provider contract, архитектурные принципы, риски и финальный технический MVP plan.

## Порядок чтения

1. `00-strategy/00-familymanager-concept-roadmap.md`
2. `00-strategy/01-familymanager-technical-stack.md`
3. `01-mvp/00-index.pplx.md`
4. `01-mvp/01-mvp-prd.pplx.md`
5. `01-mvp/02-mvp-scope-matrix.pplx.md`
6. `01-mvp/03-personas-jtbd.pplx.md`
7. `01-mvp/04-domain-model.pplx.md`
8. `01-mvp/05-metadata-schema.pplx.md`
9. `01-mvp/06-user-flows.pplx.md`
10. `01-mvp/07-ux-ia.pplx.md`
11. `01-mvp/08-architecture-principles.pplx.md`
12. `01-mvp/09-provider-contract.pplx.md`
13. `01-mvp/10-security-data-ownership.pplx.md`
14. `01-mvp/11-nfr-qa-strategy.pplx.md`
15. `01-mvp/12-risk-register-adr-backlog.pplx.md`
16. `01-mvp/13-technical-mvp-plan.pplx.md`

## Источники истины

| Тема | Канонический документ |
| --- | --- |
| Стратегия продукта и фазирование | `00-familymanager-concept-roadmap.md` |
| Библиотеки, runtime constraints, зависимости | `01-familymanager-technical-stack.md` |
| MVP scope и acceptance | `01-mvp-prd.pplx.md` |
| Границы MVP / post-MVP / enterprise | `02-mvp-scope-matrix.pplx.md` |
| Термины и доменные сущности | `04-domain-model.pplx.md` |
| SQLite/schema/file-cache split | `05-metadata-schema.pplx.md` |
| UX и dockable panel IA | `07-ux-ia.pplx.md` |
| Архитектурные инварианты smartCon | `08-architecture-principles.pplx.md` |
| Provider abstraction | `09-provider-contract.pplx.md` |
| Security/data ownership | `10-security-data-ownership.pplx.md` |
| Test/NFR strategy | `11-nfr-qa-strategy.pplx.md` |
| ADR backlog и риски | `12-risk-register-adr-backlog.pplx.md` |
| Последовательность реализации MVP | `13-technical-mvp-plan.pplx.md` |

## Зафиксированные решения

- Основной target: Revit 2025+ / `net8.0-windows`.
- Legacy target: Revit 2019–2024 / `net48`.
- Новый модуль: `SmartCon.FamilyManager`.
- `SmartCon.FamilyManager` зависит только от `SmartCon.Core` и `SmartCon.UI`.
- Revit API вызывается только через `SmartCon.Revit`.
- MVP storage: SQLite + file cache.
- Канонический локальный root: `%APPDATA%\AGK\SmartCon\FamilyManager\`.
- База MVP: `%APPDATA%\AGK\SmartCon\FamilyManager\familymanager.db`.
- Канонические таблицы: `schema_info`, `catalog_items`, `catalog_versions`, `family_files`, `family_types`, `family_parameters`, `catalog_tags`, `attachments`, `project_usage`, `previews`.
- MVP provider: `LocalCatalogProvider`.
- Future providers: `RemoteCatalogProvider`, `CorporateCatalogProvider`, `PublicReadOnlyProvider`, `CompositeCatalogProvider`.
- Project usage в MVP хранится в локальной SQLite БД, в corporate phase — в серверной БД.

## Жёсткий запрет

FamilyManager не хранит каталог, `.rfa`, версии, metadata, теги, preview, search index, usage history или избранное в ExtensibleStorage.

ExtensibleStorage остаётся паттерном существующих модулей smartCon, но не является data plane FamilyManager.

## Перед стартом реализации

Перед написанием кода нужно утвердить ADR-FM-001, ADR-FM-003, ADR-FM-004, ADR-FM-006 и ADR-FM-007. Затем технический план MVP следует детализировать фазами из `13-technical-mvp-plan.pplx.md`.

## Статус

**Phase 12 (FamilyManager MVP) — COMPLETED (2026-04-28).**

- ADR-014 принят: `docs/adr/014-familymanager-mvp-architecture.md`
- Модели и интерфейсы добавлены в `docs/domain/models.md` и `docs/domain/interfaces.md`
- `SmartCon.FamilyManager` добавлен в `docs/architecture/solution-structure.md` и `docs/architecture/dependency-rule.md`
