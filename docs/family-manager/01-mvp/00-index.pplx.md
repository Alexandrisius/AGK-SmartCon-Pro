# FamilyManager MVP Documentation Pack

## Назначение пакета

Этот пакет документов описывает MVP модуля **FamilyManager** для smartCon как первый устойчивый инкремент будущей enterprise-платформы управления BIM-контентом.

Документы подготовлены под текущую архитектуру smartCon:

- основной целевой runtime: **Revit 2025+ / `net8.0-windows`**;
- legacy runtime: **Revit 2019–2024 / `net48`**;
- новый модуль: `SmartCon.FamilyManager`;
- зависимости модуля: только `SmartCon.Core` и `SmartCon.UI`;
- все вызовы Revit API: только через интерфейсы Core и реализации в `SmartCon.Revit`;
- локальный каталог BIM-контента: SQLite + файловый кэш;
- project linkage / usage history: локальная БД каталога, а в серверной фазе серверная БД;
- серверный каталог: через provider abstraction, не как обязательная зависимость MVP.

Revit 2025 API перешёл на .NET 8, поэтому FamilyManager должен проектироваться как `net8-first` модуль с legacy-совместимостью, а не как продукт, ограниченный только `net48` ([Autodesk Development Requirements](https://help.autodesk.com/view/RVT/2025/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Welcome_to_the_Revit_Platform_API_Development_Requirements_html)).

## Документы

| № | Документ | Зачем нужен |
| --- | --- | --- |
| 01 | `01-mvp-prd.pplx.md` | Продуктовая рамка MVP, цели, non-goals, требования и критерии успеха |
| 02 | `02-mvp-scope-matrix.pplx.md` | Чёткое разделение MVP, post-MVP и enterprise-функций |
| 03 | `03-personas-jtbd.pplx.md` | Пользователи, роли и Jobs To Be Done |
| 04 | `04-domain-model.pplx.md` | Термины, сущности, границы доменной модели |
| 05 | `05-metadata-schema.pplx.md` | Метаданные семейства, локальная/серверная БД, project usage и правила версионирования |
| 06 | `06-user-flows.pplx.md` | Основные пользовательские сценарии и edge cases |
| 07 | `07-ux-ia.pplx.md` | Dockable panel, экраны, состояния UI и навигация |
| 08 | `08-architecture-principles.pplx.md` | Архитектурные принципы и repo-specific constraints |
| 09 | `09-provider-contract.pplx.md` | Контракты local/remote providers и capabilities |
| 10 | `10-security-data-ownership.pplx.md` | Владение данными, безопасность, токены, логи и IP-контент |
| 11 | `11-nfr-qa-strategy.pplx.md` | Нефункциональные требования и стратегия тестирования |
| 12 | `12-risk-register-adr-backlog.pplx.md` | Реестр рисков и список ADR перед реализацией |
| 13 | `13-technical-mvp-plan.pplx.md` | Финальный технический план MVP на основе всех предыдущих документов |

## Главная продуктовая граница MVP

MVP FamilyManager должен дать пользователю **локальный BIM-каталог семейств внутри smartCon**, где можно:

- импортировать `.rfa` файл или папку;
- сохранить метаданные в локальной SQLite базе;
- хранить физические файлы и preview в файловом кэше;
- искать и фильтровать семейства;
- открыть карточку семейства;
- загрузить семейство в активный Revit-проект;
- записать факт загрузки семейства в локальную БД каталога;
- не потерять каталог после обновления smartCon;
- подготовить архитектуру к будущему remote/corporate provider.

## Главная архитектурная граница MVP

В MVP реализуется только `LocalCatalogProvider`, но все публичные application services проектируются так, чтобы позже добавить:

- `RemoteCatalogProvider`;
- `CorporateCatalogProvider`;
- `PublicReadOnlyProvider`;
- `CompositeCatalogProvider`.

При этом MVP не должен требовать сервер, авторизацию, SSO, OpenSearch, workflow approvals или marketplace.

## Что считается готовностью пакета

Пакет документов считается готовым, если:

- MVP scope не пересекается с enterprise scope;
- доменные термины не конфликтуют с Revit API терминами;
- каталог, metadata, версии, теги, preview, поиск и usage history не хранятся в ExtensibleStorage;
- provider contract допускает серверную реализацию без переписывания UI;
- UX учитывает dockable panel lifecycle;
- NFR учитывают multi-version matrix `R19/R21/R24/R25/R26`;
- Risk Register покрывает Revit family editing, storage, performance, net48 legacy и security;
- технический план идёт последним и ссылается на предыдущие документы.
