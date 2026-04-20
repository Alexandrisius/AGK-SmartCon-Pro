# Roadmap — Фазы разработки

> Загружать: при планировании работ.

---

## Граф зависимостей

```
Фаза 0 --> Фаза 1 --> Фаза 2 --+--> Фаза 3 --+--> Фаза 5 --+
                                |              |              |
                                +--> Фаза 4 --+              +--> Фаза 8 --> Фаза 9
                                |                             |
                                +--> Фаза 7 -----------------+
                                |
               Фаза 1 ---------+--> Фаза 6 ---------------------> Фаза 9
```

---

## Сводная таблица

| Фаза | Название | Зависит от | Ключевой результат | Статус |
|---|---|---|---|---|
| **0** | Каркас проекта | — | Solution + Ribbon + DI | ✅ Готов |
| **1** | Фундамент | 0 | Модели + интерфейсы + базовые сервисы | ✅ Готов |
| **2** | Базовый коннект | 1 | Клик-клик -> выравнивание -> ConnectTo | ✅ Готов |
| **3** | Типы коннекторов | 2 | MiniTypeSelector + окно маппинга + ExtensibleStorage (ADR-012) | ✅ Готов |
| **4** | Подбор параметров | 2 | Автоподбор размера / типоразмера | ✅ Готов |
| **5** | Система фитингов | 3, 4 | Автовставка фитинга по маппингу | ✅ Готов |
| **6** | FormulaSolver | 1 | Полноценный AST-парсер формул Revit | ✅ Готов |
| **7** | Цепочки (Chain) | 2 | Перемещение всей сети как жёсткого тела | ✅ Готов |
| **8** | Финальное окно | 5, 7 | PipeConnectEditor — PostProcessing UI | ✅ Готов |
| **9** | Рефакторинг | 8 | ViewModel 631→384 строк, 12 handler-классов | ✅ Готов |
| **10** | Open-source качество | 9 | Локализация, XML-docs, качество кода | ✅ Готов |

---

## Фаза 0 — Каркас проекта

**Цель:** Пустой работающий Solution, сборка, DI, Ribbon-кнопка.

- Создать Solution SmartCon.sln со всеми проектами
- Directory.Build.props (net8.0-windows, C# 12, Nullable)
- NuGet: Revit API 2025, MEDI, xUnit, Moq
- App.cs (IExternalApplication)
- RibbonBuilder.cs — кнопки PipeConnect + Настройки
- ServiceLocator + ServiceRegistrar (MEDI)
- .addin манифест
- RevitContext (IRevitContext)

**Приёмка:** Solution компилируется. Плагин загружается в Revit. Кнопка на Ribbon.

**Статус:** ✅ Готов (2026-03-25). Сборка 0 ошибок, 2 теста pass, DLL деплоятся в Revit Addins.

---

## Фаза 1 — Фундамент

**Цель:** Все доменные модели, интерфейсы, базовые инфраструктурные сервисы.

- Все модели из `domain/models.md`
- Все интерфейсы из `domain/interfaces.md`
- RevitTransactionService + SmartConFailurePreprocessor
- ConnectorWrapper, Extensions (Connector, Element, XYZ)
- VectorUtils (CrossProduct, AngleBetween, IsParallel)
- Unit-тесты на модели и VectorUtils

**Приёмка:** Модели + интерфейсы компилируются. Core чист (I-09). Тесты проходят.

**Статус:** ✅ Готов (2026-03-26). Сборка 0 ошибок, 54 теста pass (+ 7 RevitRequired). Vec3 + VectorUtils, 10 моделей, 11 интерфейсов, ConnectorWrapper, Extensions. ADR-009.

---

## Фаза 2 — Базовый коннект

**Цель:** Минимальный рабочий сценарий: 2 клика = соединение.

- FreeConnectorFilter (ISelectionFilter)
- ElementSelectionService.PickElementWithPoint
- Алгоритм «ближайший свободный коннектор к точке клика»
- PipeConnectExternalEvent (IExternalEventHandler)
- PipeConnectCommand (IExternalCommand)
- ConnectorAligner (алгоритм из `pipeconnect/algorithms.md`)
- Workflow S1 -> S2 -> S3 -> Committed (без S4/S5/S6)

**Приёмка:** Два клика -> элемент перемещён -> ConnectTo. ESC = отмена.

**Статус:** ✅ Готов (2026-03-30). Сборка 0 ошибок, 69 тестов pass. FreeConnectorFilter, ElementSelectionService, ConnectorAligner (4 шага), ITransformService + RevitTransformService, IConnectorService + ConnectorService, PipeConnectExternalEvent, IRevitUIContext, Vec3-свойства в ConnectorProxy. ConnectorAlignerTests (15 тестов).

---

## Фаза 3 — Типы коннекторов

**Цель:** ConnectionTypeCode, MiniTypeSelector, окно маппинга.

**3A — MiniTypeSelector:**
- Чтение/запись Description через EditFamily
- Справочник типов из per-project ExtensibleStorage (ADR-012)
- MiniTypeSelectorView (рядом с курсором)
- Интеграция в state machine (S1.1)

**3B — Окно маппинга:**
- MappingEditorView (модальное, ShowDialog — ADR-012)
- Вкладка «Типы» — CRUD
- Вкладка «Правила» — FromType->ToType, семейства из проекта
- Кнопки Импорт/Экспорт JSON (ручной перенос между проектами)
- Кнопка Настройки на Ribbon

**Приёмка:** Тип записывается в Description. Маппинг сохраняется в `DataStorage` проекта. Типы синхронизированы между окнами.

**Статус:** ✅ Готов (2026-04-01). Сборка 0 ошибок, тесты pass.
Ключевые компоненты: IFamilyConnectorService + RevitFamilyConnectorService (EditFamily API, LookupParameter), FittingMappingJsonSerializer (Core, System.Text.Json, pure-C#), RevitFittingMappingRepository (Revit/Storage, ExtensibleStorage), PipeConnectDialogService (IDialogService + Open/SaveFileDialog), MiniTypeSelectorView/ViewModel (модальное, рядом с курсором), MappingEditorView/ViewModel (модальное, TabControl + Import/Export), ConnectorTypeItem + MappingRuleItem (ObservableObject), SettingsCommand реализован.
Тесты: FittingMappingJsonSerializerTests (15), MiniTypeSelectorViewModelTests, MappingEditorViewModelTests, ConnectorTypeItemTests, MappingRuleItemTests.

**Миграция (ADR-012, 2026-04-19):**
- Хранилище перенесено из `%APPDATA%\AGK\SmartCon\connector-mapping.json`
  в `ExtensibleStorage.DataStorage` каждого `.rvt`.
- Settings окно переведено на модальный `ShowDialog` с Owner = Revit main window.
- Добавлены кнопки Импорт/Экспорт JSON; диалог Импорта по умолчанию открывается
  в AppData-папке со старым `connector-mapping.json`, если файл существует.
- Авто-миграция из AppData не делается — перенос только по явному действию пользователя.

---

## Фаза 4 — Подбор параметров

**Цель:** Автоподбор размера при несовпадении диаметров.

- MiniFormulaSolver (парсер формул: Evaluate, SolveFor, ParseSizeLookup, ExtractVariables)
- FamilyParameterAnalyzer (анализ цепочки зависимостей через EditFamily)
- RevitParameterResolver (GetConnectorRadiusDependencies, TrySetConnectorRadius)
- RevitLookupTableService (парсинг size_lookup, поиск радиуса)
- Логика S4 в PipeConnectCommand (BuildResolutionPlan, ApplyParameterResolution)
- SubTransaction для превью (подбор ChangeTypeId без commit)
- PipeConnectionSession: NeedsAdapter, OriginalDynamicRadius, ActualDynamicRadius
- ParameterDependency: DirectParamName, RootParamName

**Приёмка:** Разные диаметры -> автоподбор. LookupTable парсится. Смена типоразмера работает.

**Статус:** ✅ Готов (2026-04-01). Сборка 0 ошибок, 201 тест pass (208 всего, 7 pre-existing RevitAPI не в output).
Компоненты: MiniFormulaSolver (30+ тестов), FamilyParameterAnalyzer, RevitParameterResolver, RevitLookupTableService, интеграция S4 в PipeConnectCommand.
Тесты: MiniFormulaSolverTests (30+), ParameterResolutionFlowTests (18+).

**Статус:** ✅ Готов. Автовставка фитинга, FittingMapper, IFittingInsertService, RevitFittingInsertService. Фильтрация по размерам. Прямое соединение для isDirectConnect.

---

## Фаза 6 — FormulaSolver

**Статус:** ✅ Готов. AST-парсер (Tokenizer + Parser), Evaluate, SolveFor (IF-упрощение + алгебра + бисекция), ParseSizeLookup. 30+ тестов.

---

## Фаза 7 — Цепочки (Chain)

**Статус:** ✅ Готов. ElementChainIterator (BFS через AllRefs), ConnectionGraph, NetworkMover, INetworkMover. Обход линейных + ветвящихся цепочек. Перемещение как жёсткое тело.

---

## Фаза 8 — Финальное окно

**Статус:** ✅ Готов. PipeConnectEditorView (немодальное), поворот, смена коннектора, список фитингов, Connect/Cancel через TransactionGroup Assimilate/RollBack. ExternalEvent pattern.

---

## Фаза 9 — Рефакторинг

**Цель:** Декомпозиция ViewModel, улучшение качества кода.

**Ключевые результаты:**
- ViewModel 631→384 строк (5 partial файлов)
- 12 handler-классов выделены из ViewModel
- DynamicSizeLoader, ConnectorCycleService, FittingCardBuilder
- ConnectExecutor, ChainOperationHandler, PositionCorrector
- PipeConnectInitHandler, PipeConnectRotationHandler, PipeConnectSizeHandler
- 577 тестов, 0 регрессий

**Статус:** ✅ Готов (ветка refactoring-1).

---

## Фаза 10 — Open-source качество

**Цель:** Локализация, XML-документация, качество кода.

**Ключевые результаты:**
- LocalizationService (RU/EN переключение)
- LanguageManager (ResourceDictionary swap)
- XAML DynamicResource для всех UI-строк
- LocalizationService.GetString() для всех C#-строк
- XML-docs для всех публичных типов
- Ротация логов, константы вместо магических чисел
- CHANGELOG.md обновлён

**Статус:** ✅ Готов.


---

## Phase 5 — OSS Perfection (2026-04-19)

**Цель:** Привести проект к состоянию образцового open-source-репозитория.

**Фазы A-J** (см. `.opencode/plans/oss-perfection-plan.md`):

- **A.** Устранение Service Locator — ViewModelFactory-интерфейсы + DI
- **B.** Чистая архитектура слоёв (I-09 соответствие, dedup BoolToVisibilityConverter, ConnectionGraphBuilder)
- **C.** MVVM discipline — DialogWindowBase, IDialogPresenter, XAML named colors, IFittingCtcSetupItem в Core
- **D.** XML-documentation на публичном API + WPF-namings
- **E.** Multi-version + CI matrix build.yml / release.yml (R19/R21/R24/R25)
- **F.** Покрытие тестами 577 → 676 (формулы, state machine, PipeConnect-сервисы)
- **G.** Dependabot-совместимые обновления NuGet
- **H.** Актуализация всей документации — ExtensibleStorage, I-01..I-13, test count, solution structure
- **I.** OSS-ready — CONTRIBUTING, CHANGELOG, .github templates, CODEOWNERS, future-work.md
- **J.** Code polish — `dotnet format`, `sealed` classes audit, NoWarn review

**Ключевые результаты:**
- Сборка чистая на R25 / R24 / R21 / R19 (net8.0 + net48)
- 676 тестов pass, 0 регрессий
- Инварианты I-01..I-13 соблюдены
- Документация SSOT синхронизирована с кодом
- `build-and-deploy.bat` — единый способ сборки

**Статус:** ✅ Готов (2026-04-19).
