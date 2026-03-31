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
| **3** | Типы коннекторов | 2 | MiniTypeSelector + окно маппинга + JSON | ✅ Готов |
| **4** | Подбор параметров | 2 | Автоподбор размера / типоразмера | ✅ Готов |
| **5** | Система фитингов | 3, 4 | Автовставка фитинга по маппингу | Не начат |
| **6** | FormulaSolver | 1 | Полноценный парсер формул Revit | Не начат |
| **7** | Цепочки (Chain) | 2 | Перемещение всей сети как жёсткого тела | Не начат |
| **8** | Финальное окно | 5, 7 | PipeConnectEditor — PostProcessing UI | Не начат |
| **9** | Продвинутое | 8, 6 | Дейкстра, труба/арматура, DN-фильтры, инсталлятор | Не начат |

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
- JSON-конфиг типов из AppData
- MiniTypeSelectorView (рядом с курсором)
- Интеграция в state machine (S1.1)

**3B — Окно маппинга:**
- MappingEditorView (немодальное)
- Вкладка «Типы» — CRUD
- Вкладка «Правила» — FromType->ToType, семейства из проекта
- Кнопка Настройки на Ribbon

**Приёмка:** Тип записывается в Description. Маппинг сохраняется в JSON. Типы синхронизированы.

**Статус:** ✅ Готов (2026-04-XX). Сборка 0 ошибок, 120 тестов pass.
Ключевые компоненты: IFamilyConnectorService + RevitFamilyConnectorService (EditFamily API, LookupParameter), JsonFittingMappingRepository (Core, System.Text.Json), PipeConnectDialogService (IDialogService), MiniTypeSelectorView/ViewModel (модальное, рядом с курсором), MappingEditorView/ViewModel (немодальное, TabControl), ConnectorTypeItem + MappingRuleItem (ObservableObject), SettingsCommand реализован.
Тесты: JsonFittingMappingRepositoryTests (8), MiniTypeSelectorViewModelTests (8), MappingEditorViewModelTests (16), ConnectorTypeItemTests (5), MappingRuleItemTests (10).

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

**Статус:** ✅ Готов (2026-04-XX). Сборка 0 ошибок, 201 тест pass (208 всего, 7 pre-existing RevitAPI не в output).
Компоненты: MiniFormulaSolver (30+ тестов), FamilyParameterAnalyzer, RevitParameterResolver, RevitLookupTableService, интеграция S4 в PipeConnectCommand.
Тесты: MiniFormulaSolverTests (30+), ParameterResolutionFlowTests (18+).

---

## Фаза 5 — Система фитингов

**Цель:** Автоматический подбор и вставка фитингов.

- FittingMapper (GetMappings)
- Фильтрация по размерам коннекторов
- Вставка FamilyInstance + позиционирование
- Автовыбор по приоритету

**Приёмка:** TYPE-1 + TYPE-2 -> фитинг вставлен. Фильтрация работает. Прямое соединение для isDirectConnect.

---

## Фаза 6 — FormulaSolver

**Цель:** Универсальный парсер формул Revit.

- Tokenizer + AST Parser
- Evaluate (арифметика, if, trig, единицы)
- SolveFor (алгебраическая инверсия + бисекция)
- ParseSizeLookup
- 30+ unit-тестов

**Приёмка:** Все тестовые формулы вычисляются корректно. SolveFor решает линейные и нелинейные.

---

## Фаза 7 — Цепочки (Chain)

**Цель:** Перемещение подключённой сети.

- ElementChainIterator (BFS через AllRefs)
- BuildGraph с обработкой разветвлений
- Transform всех Nodes как жёсткого тела
- Toggle «Переместить всю сеть»

**Приёмка:** BuildGraph обходит линейные + ветвления. Нет зацикливания. Перемещение работает.

---

## Фаза 8 — Финальное окно

**Цель:** PipeConnectEditor — полный PostProcessing UI.

- Немодальное WPF-окно
- Поворот (произвольный угол, шаг, hotkeys)
- Смена коннектора (переалайн)
- Список фитингов (примерить = реальная вставка)
- Соединить (Assimilate) / Отмена (RollBack)
- Все действия через ExternalEvent

**Приёмка:** Окно немодальное. Поворот/смена/фитинги работают. Одна Undo-запись.

---

## Фаза 9 — Продвинутое

- **9A:** Дейкстра для цепочки фитингов (PathfinderService)
- **9B:** Вставка трубы / арматуры из финального окна
- **9C:** Фильтры по DN в маппинге (MinDN, MaxDN)
- **9D:** Полноценный инсталлятор (WiX / Inno Setup)
