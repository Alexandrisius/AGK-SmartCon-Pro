# ADR-028: DI Readiness Audit (для feature/di-readiness ветки)

**Status:** accepted (audit only)
**Date:** 2026-06-09
**Branch:** feature/logging-improvements (audit)
**Target branch:** feature/di-readiness (full migration)

## Контекст

В `feature/logging-improvements` сделан **только audit** текущего DI состояния.
Полная миграция — в **отдельной ветке** `feature/di-readiness` после merge.
Этот ADR — точка входа для будущей работы.

## Текущее состояние (на 2026-06-09)

### 1. Service Locator pattern — 15 мест

`ServiceHost.GetService<T>()` — Service Locator anti-pattern. Используется в
**15 местах**, типично в Commands и Helper-сервисах.

| Файл | Метод | Сервис |
|---|---|---|
| `App.cs:55` | `OnStartup` | `FamilyManagerPaneProvider` |
| `App.cs:69` | `OnStartup` | `RibbonBuilder` (static) |
| `ShareProjectCommand.cs:44` | `Execute` | `IShareProjectSettingsRepository` |
| `ShareProjectCommand.cs:288` | `Execute` | `IModelPurgeService` |
| `ShareSettingsCommand.cs` | `Execute` | factory (уже) |
| `AboutViewModel` ctor | — | factory (уже) |
| `EditFamilySession.cs` | (ctor DI) | уже DI |
| ... (ещё 9) | | |

**Решение:** ADR-028 **откладывает** полный переход. Часть миграции уже
сделана через ViewModel Factories (`IAboutViewModelFactory`,
`IShareSettingsViewModelFactory`, etc.) — это **правильный** паттерн.

### 2. Static class helpers — 11 (все обоснованы)

| Класс | Обоснование static |
|---|---|
| `LocalizationService` (5 partial) | Pure string lookup, no state, by design |
| `LanguageManager` | WPF Application.Current integration |
| `CursorHelper` | Native interop, no DI lifetime |
| `FamilyManagerPaneIds` | Constants only |
| `CommandHelper` | Static helper для IExternalCommand |
| `LogScopeProvider` | AsyncLocal-based, by design (Phase 0a) |
| `DialogCloseHelper` | WPF Window helper |

**Решение:** оставить как есть. Все имеют чёткое обоснование.

### 3. ServiceRegistrar — comprehensive

`src\SmartCon.App\DI\ServiceRegistrar.cs` содержит **~80 регистраций**:
- 70 singleton (включая Revit-обёртки)
- 7 ViewModel Factories
- 6 View → ViewModel mappings (WpfDialogPresenter)
- 2 ExternalEvent handlers
- IClock, IIdGenerator, IDispatcher (Phase 5+6)

**Качество:** высокое. Все сервисы с интерфейсами, нет "Hidden Globals".

## Что УЖЕ сделано (post-Phase 7, ADR-025)

- ✅ `IFamilyManagerServices` record (30+ props) — DI-инжектируется в ViewModels
- ✅ `IConnectorService`, `ITransformService`, `IAlignmentService`
- ✅ `IFamilySearchService`, `IFamilyLoadService`, `IFamilyPlacementService`
- ✅ `IDispatcher`, `IClock`, `IIdGenerator` (Phase 5+6 refactoring)
- ✅ `ISmartConLogger` (Phase 2 logging) — для будущей DI-интеграции
- ✅ `AsyncBridge` (C5) — pure static, by design

## Что ОСТАЛОСЬ для полной DI-готовности

### Tier 1 — низкорискованные (1-2 дня)

1. **Заменить 15 `ServiceHost.GetService<>`** в Commands на ctor-инжекцию
   - Плюсы: убирает Service Locator anti-pattern
   - Минусы: ломает существующие Command'ы (нужно пересоздать через DI)
   - Пример: `ShareProjectCommand` → `IShareProjectSettingsRepository` в ctor

2. **Document `ServiceRegistrar`** с auto-doc через XML comments

### Tier 2 — средне-рискованные (3-5 дней)

3. **Audit ViewModels на `static class` usage** — заменить где возможно
   - Например, `LocalizationService.GetString(...)` — OK (pure static)
   - `LanguageManager.SwitchLanguage(...)` — OK (WPF global)
   - **НЕ** делать Tier 2 для всех — есть legitimate static usages

4. **Ввести `IViewServices` record** — DI-friendly View helpers
   - `ShowMessage`, `ShowDialog`, etc.
   - Сейчас прямой WPF `MessageBox.Show()` или `_dialogService`

### Tier 3 — высоко-рискованные (отдельный ADR)

5. **Полная замена WPF Application.Current на IDispatcher** — 7 мест
   - `LanguageManager`, `CursorHelper`, `EditFamilySession`
   - Делать **только** если понадобится unit-тестирование WPF-кода

## План миграции (для `feature/di-readiness`)

### Phase 7.1 (1-2 дня) — Commands

```csharp
// BEFORE
public ShareProjectCommand()
{
    _settingsRepo = ServiceHost.GetService<IShareProjectSettingsRepository>();
}

// AFTER (через factory или DI-инжекцию в ctor)
public ShareProjectCommand(IShareProjectSettingsRepository settingsRepo, ...)
{
    _settingsRepo = settingsRepo;
}
```

### Phase 7.2 (2-3 дня) — Tier 2 services

- Audit `static class XHelper` — какие могут быть instance?
- Заменить на interface + DI registration

### Phase 7.3 (3-5 дней) — Test coverage

- Без DI нельзя замокать static classes
- С DI — `Mock<IShareProjectSettingsRepository>()`
- Tier 1+2 даст ~80% testability

## Acceptance criteria (для `feature/di-readiness`)

| Метрика | Цель |
|---|---|
| `ServiceHost.GetService<>` | 0 в production |
| `static class XService` (без обоснования) | 0 (оставить только 11 обоснованных) |
| Commands с ctor-инжекцией | 100% |
| ViewModels с ctor-инжекцией | 100% (вместо static) |
| Tests с моками | ≥50% (вместо unit-тестов только на Core) |
| Build | 4/4 configs зелёные |

## Почему НЕ делаем в `feature/logging-improvements`

| Причина | Объяснение |
|---|---|
| **Размер PR** | Tier 1+2 = 1-2 недели работы. Phase L+5+6+7 уже 5 дней |
| **Регрессионный риск** | DI-инжекция в Commands ломает plugin loading |
| **Тестирование** | Требует ручного теста в Revit (как и Phase 5.1) |
| **Обзор кода** | 1 большой PR тяжело review; лучше несколько маленьких |

## Out of scope (отложено навсегда)

- ❌ Полная замена `LocalizationService` на IStringLocalizer (Microsoft)
- ❌ Microsoft.Extensions.DependencyInjection → Autofac/StructureMap (overkill)
- ❌ WPF MVVM Toolkit migration (CommunityToolkit уже используется)

## Связанные документы

- [ADR-025](025-refactoring-migration-backlog.md) — M-019-001..005 (5 миграций)
- [ADR-027](027-placed-families-v2.md) — DI patterns в новом коде
- [docs/architecture/dependency-injection.md](../architecture/dependency-injection.md) — TBD
- [docs/domain/interfaces.md](../domain/interfaces.md) — IClock, IIdGenerator, IDispatcher
- [.agents/skills/revit-api-best-practice](../../.agents/skills/revit-api-best-practice/SKILL.md) — DI в Revit context

## Audit method

```bash
# Service Locator count
rg -t cs "ServiceHost\.GetService" src/ -c

# Static services count
rg -t cs "static (partial )?class \w+(Service|Manager|Helper|Provider)" src/

# Service Registrations
Get-Content src/SmartCon.App/DI/ServiceRegistrar.cs | Select-String "AddSingleton|AddScoped|AddTransient"
```

Просмотрено вручную: 80+ регистраций, 15 ServiceHost calls, 11 static services.
**Никаких новых DI-готовых инфраструктур не требуется** — всё есть.
