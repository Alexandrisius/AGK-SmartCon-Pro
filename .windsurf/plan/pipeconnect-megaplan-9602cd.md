# PipeConnect — Мега-план разработки

Полный roadmap разработки флагманского модуля PipeConnect приложения SmartCon для Revit 2025 (.NET 8 / C# 12 / WPF) — от фундамента архитектуры до продвинутых функций, разбитый на 9 фаз.

---

## Общие сведения

| Параметр | Значение |
|---|---|
| **Платформа** | Revit 2025, .NET 8, C# 12, WPF |
| **DI** | Microsoft.Extensions.DependencyInjection |
| **Тесты** | xUnit + Moq |
| **Хранилище маппинга** | JSON (глобальный, AppData) |
| **Целевой пользователь** | MEP-инженер-проектировщик |
| **Ключевая боль** | Revit не умеет удобно соединять элементы в 3D. Нет системы типов соединений. Нет умного подбора фитингов. |
| **Scope** | PipeConnect + настройки приложения. RotateElements/AlignElements вне scope. |

---

## Архитектура Solution

```
SmartCon.sln
+-- SmartCon.Core              <- Чистый C#. Модели, интерфейсы, алгоритмы. Без Revit/WPF.
+-- SmartCon.Revit             <- Реализации интерфейсов Core через Revit API.
+-- SmartCon.UI                <- WPF-библиотека: стили, контролы, базовые VM-классы, RelayCommand.
+-- SmartCon.App               <- Точка входа: IExternalApplication, Ribbon, DI-контейнер.
+-- SmartCon.PipeConnect       <- Плагин: Commands, ViewModels, Views модуля PipeConnect.
+-- SmartCon.Tests             <- Unit + ViewModel тесты (xUnit + Moq).
+-- SmartCon.Installer         <- Инсталлятор (копирует DLL + .addin в папки Revit).
```

**Dependency Rule:** Все проекты зависят от Core. Никто не ссылается на Revit напрямую из UI или плагинов.

---

## Фаза 0 — Каркас проекта

**Цель:** Пустой работающий Solution, сборка, DI и Ribbon-кнопка.

### Задачи

- **0.1** Создать Solution SmartCon.sln со всеми проектами
- **0.2** Directory.Build.props (TargetFramework, LangVersion, Nullable)
- **0.3** NuGet: Revit API 2025, MEDI, xUnit, Moq
- **0.4** App.cs (IExternalApplication)
- **0.5** RibbonBuilder.cs — кнопка "PipeConnect" + кнопка "Настройки" на ленте
- **0.6** ServiceLocator.cs + ServiceRegistrar.cs (MEDI)
- **0.7** .addin манифест-файл
- **0.8** RevitContext.cs (IRevitContext)
- **0.9** Базовый инсталлятор-скрипт

### Приёмка
- Solution компилируется. Плагин загружается в Revit 2025.
- Кнопка PipeConnect на Ribbon. Нажатие = TaskDialog.
- DI резолвит IRevitContext.

---

## Фаза 1 — Фундамент: модели, сервисы, инварианты

**Цель:** Все доменные модели, интерфейсы и базовые инфраструктурные сервисы.

### Задачи

- **1.1** ConnectorProxy record
- **1.2** ConnectionTypeCode readonly record struct
- **1.3** PipeConnectionSession (мутабельный контекст сессии)
- **1.4** PipeConnectState enum
- **1.5** ConnectionGraph + ConnectionEdge
- **1.6** FittingMapping record
- **1.7** Все интерфейсы: IRevitContext, ITransactionService, IElementSelectionService, IFittingMapper, IFormulaSolver, IParameterResolver, ILookupTableService, IElementChainIterator
- **1.8** RevitTransactionService (RunInTransaction + RunInTransactionGroup)
- **1.9** SmartConFailurePreprocessor (IFailuresPreprocessor)
- **1.10** ConnectorWrapper — утилита создания ConnectorProxy из Revit.DB.Connector
- **1.11** Extension-методы: ConnectorExtensions, ElementExtensions, XYZExtensions
- **1.12** VectorUtils.cs — CrossProduct, AngleBetween, IsParallel, IsAntiParallel
- **1.13** Unit-тесты на модели и VectorUtils

### Приёмка
- Все модели и интерфейсы из SSOT реализованы.
- RevitTransactionService: commit при успехе, rollback при исключении.
- Core не содержит using Autodesk.Revit.DB (инвариант I-09).
- Unit-тесты проходят.

---

## Фаза 2 — Базовый коннект: выбор -> выравнивание -> ConnectTo

**Цель:** Минимальный рабочий сценарий: 2 клика = соединение одного элемента.

### Задачи

- **2.1** FreeConnectorFilter (ISelectionFilter) — только элементы с free-коннектором
- **2.2** ElementSelectionService.PickElementWithPoint
- **2.3** Алгоритм "ближайший свободный коннектор к точке клика"
- **2.4** PipeConnectExternalEvent (IExternalEventHandler)
- **2.5** PipeConnectCommand (IExternalCommand)
- **2.6** ConnectorAligner.cs — алгоритм выравнивания (Core, чистая математика)
- **2.7** Реализация Transform через Revit API (MoveElement + RotateElement)
- **2.8** Базовый workflow: S1 -> S2 -> S3 -> Committed (без S4/S5/S6)
- **2.9** ConnectTo после выравнивания

### Алгоритм выравнивания (ConnectorAligner)

1. **Перемещение** — offset = static.Origin - dynamic.Origin, MoveElement
2. **Поворот по BasisZ** — антипараллельность: targetZ = -static.BasisZ
   - Ось поворота: axis = dynamic.BasisZ x targetZ, угол: acos(dot)
   - Особый случай: коллинеарны и сонаправлены -> разворот 180 вокруг перпендикулярной оси
3. **Снэп BasisX к "красивому" углу** — округление до ближайшего кратного 15 градусов (покрывает 30, 45, 60, 90). Пользователь видит "чистый" угол который легко скорректировать.
4. **Коррекция позиции** — после поворота Origin мог сместиться, пересчитать offset.

### Приёмка
- Клик по элементу без свободных коннекторов игнорируется.
- ESC на любом этапе = отмена.
- Два клика -> динамический элемент перемещается, оси Z антипараллельны, Origins совпадают.
- ConnectTo успешен (элементы в одной системе).

---

## Фаза 3 — Система типов коннекторов

**Цель:** ConnectionTypeCode, MiniTypeSelector и окно управления маппингом.

### 3A — ConnectionTypeCode и MiniTypeSelector

- **3A.1** Чтение Description коннектора -> парсинг в ConnectionTypeCode
- **3A.2** Запись ConnectionTypeCode в Description через программное открытие семейства (EditFamily -> изменить Description -> LoadFamily обратно)
- **3A.3** JSON-конфиг типов: {code, name, description}
- **3A.4** Сервис загрузки/сохранения конфига из AppData
- **3A.5** MiniTypeSelectorView.xaml — компактное окно рядом с курсором
- **3A.6** MiniTypeSelectorViewModel
- **3A.7** Интеграция в state machine: S1.1 (проверка Description, если пустой -> MiniTypeSelector)

**Техническая заметка по записи Description:**
1. doc.EditFamily(family) -> FamilyDocument
2. Найти ConnectorElement по индексу в FamilyDocument
3. Изменить Description
4. familyDoc.LoadFamily(doc, FamilyLoadOptions) -> перезагрузить
5. Всё в одной Transaction

### 3B — Окно управления маппингом

- **3B.1** Модель ConnectorTypeDefinition (code, name, description)
- **3B.2** Модель FittingMappingRule (FromType, ToType, isDirectConnect, список семейств, Priority)
- **3B.3** IFittingMappingRepository (CRUD)
- **3B.4** JsonFittingMappingRepository (AppData)
- **3B.5** MappingEditorView.xaml — немодальное окно
- **3B.6** MappingEditorViewModel
- **3B.7** Вкладка "Типы" — CRUD таблица
- **3B.8** Вкладка "Правила" — таблица FromType->ToType, семейства, приоритет
- **3B.9** Выбор семейств из загруженных в проект (ComboBox FamilySymbol)
- **3B.10** Кнопка "Настройки SmartCon" на Ribbon
- **3B.11** Новые типы сразу видны в MiniTypeSelector

### Структура JSON маппинга (AppData)
```json
{
  "connectorTypes": [
    { "code": 1, "name": "Сварка" },
    { "code": 2, "name": "Резьба" },
    { "code": 3, "name": "Раструб" }
  ],
  "mappingRules": [
    {
      "fromTypeCode": 1, "toTypeCode": 1,
      "isDirectConnect": true,
      "fittingFamilies": [
        { "familyName": "СварнойШов", "symbolName": "*", "priority": 1 }
      ]
    },
    {
      "fromTypeCode": 1, "toTypeCode": 2,
      "isDirectConnect": false,
      "fittingFamilies": [
        { "familyName": "Переходник_С-Р", "symbolName": "*", "priority": 1 }
      ]
    }
  ]
}
```

### Приёмка
- Клик на коннектор без Description -> MiniTypeSelector рядом с курсором.
- Тип записывается в Description семейства через EditFamily.
- Окно маппинга открывается кнопкой Настройки.
- Типы и правила сохраняются/загружаются из JSON.
- Семейства выбираются из проекта.

---

## Фаза 4 — Подбор параметров (ResolvingParameters)

**Цель:** При несовпадении размеров коннекторов — автоподбор типоразмера или изменение параметра.

### Задачи

- **4.1** RevitParameterResolver (IParameterResolver)
- **4.2** GetConnectorRadiusDependencies — анализ зависимых параметров
- **4.3** RevitLookupTableService (ILookupTableService)
- **4.4** Парсинг size_lookup для определения порядка столбцов CSV
- **4.5** ConnectorRadiusExistsInTable
- **4.6** GetNearestAvailableRadius
- **4.7** TrySetConnectorRadius (с учётом зависимостей)
- **4.8** Логика S4: сравнение радиусов -> смена типоразмера / параметра / ближайший
- **4.9** SubTransaction для превью изменения
- **4.10** Unit-тесты

### Алгоритм S4

```
1. static.Radius == dynamic.Radius -> пропустить, S5
2. deps = GetConnectorRadiusDependencies(dynamic)
3. LookupTable: размер есть? -> установить -> готово
4. LookupTable: размера нет -> GetNearestAvailableRadius -> пометить "нужен переходник"
5. Параметр экземпляра без формулы -> прямая запись
6. Параметр экземпляра с формулой -> FormulaSolver.SolveFor (Фаза 6)
7. Параметр типа -> перебор TypeId -> смена типа
8. Ничего не помогло -> уведомление
```

### Приёмка
- Разные диаметры -> автоподбор размера.
- LookupTable парсится (столбцы из size_lookup).
- Смена типоразмера работает.
- SubTransaction не оставляет мусора.

---

## Фаза 5 — Система фитингов

**Цель:** Автоматический подбор и вставка фитингов по маппингу.

### Задачи

- **5.1** FittingMapper (IFittingMapper)
- **5.2** GetMappings(from, to) по приоритету
- **5.3** Загрузка маппингов из JSON
- **5.4** Фильтрация семейств по совместимости размеров коннекторов
- **5.5** Вставка FamilyInstance фитинга (NewFamilyInstance)
- **5.6** Позиционирование фитинга (ConnectorAligner для коннекторов фитинга)
- **5.7** Логика S5: типы -> подбор -> вставка
- **5.8** Автовыбор: 1 семейство = автоматически; несколько = первый по приоритету
- **5.9** isDirectConnect без семейств = прямое ConnectTo
- **5.10** Тесты на FittingMapper

### Алгоритм вставки фитинга

```
1. GetMappings(static.TypeCode, dynamic.TypeCode)
2. isDirectConnect + нет семейств -> ConnectTo напрямую
3. isDirectConnect + есть семейства (напр. сварной шов):
   - Фильтр по размерам коннекторов
   - 1 семейство -> автовставка; >1 -> первый по приоритету
4. !isDirectConnect -> обязателен переходник, фильтр по размерам, вставка
5. Позиционирование фитинга: выравнивание его коннекторов с static/dynamic
6. ConnectTo: static<->fitting.Conn1, fitting.Conn2<->dynamic
```

### Приёмка
- TYPE-1 + TYPE-2 с правилом -> фитинг вставлен автоматически.
- Фитинг позиционирован корректно.
- Фильтрация по размерам работает.
- Прямое соединение для isDirectConnect без семейств.

---

## Фаза 6 — FormulaSolver: универсальный парсер формул

**Цель:** Полноценный модуль вычисления любых формул Revit, включая обратное решение.

### Задачи

- **6.1** Tokenizer (лексер)
- **6.2** AST-парсер (дерево выражений)
- **6.3** Операторы: +, -, *, /, ^, %, сравнения (<, >, <=, >=, =, <>)
- **6.4** Функции: if(), and(), or(), not(), abs(), round(), roundup(), rounddown()
- **6.5** Тригонометрия: sin(), cos(), tan(), asin(), acos(), atan()
- **6.6** sqrt(), pi, e, min(), max()
- **6.7** Единицы измерения (mm, m, ft, in) -> конвертация в Internal Units
- **6.8** Evaluate(formula, parameters)
- **6.9** SolveFor(formula, variableName, targetValue, otherValues)
- **6.10** ParseSizeLookup(...) — имя таблицы и порядок параметров
- **6.11** Интеграция с IParameterResolver
- **6.12** Unit-тесты (30+ кейсов)

### Архитектура

```
Строка -> [Tokenizer] -> Token[] -> [Parser] -> AST
                                                  |
                                    Evaluate / SolveFor / ParseSizeLookup
```

**SolveFor:** Линейные формулы -> алгебраическая инверсия AST. Сложные (if, trig) -> бисекция/Ньютон. Округление до 6 знаков.

### Приёмка
- Evaluate("if(x < 100, x / 2 - 1, x / 2 - 2)", {x: 80}) = 39
- Evaluate("sin(pi / 2)") = 1.0
- SolveFor("Radius = DN / 2 - 1", "DN", 24.0, {}) = 50.0
- ParseSizeLookup("size_lookup(Table1, ..., DN, PN, Type)") = ["DN", "PN", "Type"]
- 30+ unit-тестов проходят.

---

## Фаза 7 — Поддержка цепочек (Chain)

**Цель:** Перемещение всей подключённой сети как жёсткого тела.

### Задачи

- **7.1** ElementChainIterator (IElementChainIterator) — BFS через коннекторы
- **7.2** BuildGraph: рекурсивный обход AllRefs, построение ConnectionGraph
- **7.3** Обработка разветвлений (тройники) — все ветки в граф
- **7.4** Защита от циклов (visited set)
- **7.5** GetChainEndConnectors — свободные коннекторы на границах
- **7.6** ConnectorAligner: Transform ко всем Nodes графа
- **7.7** Кнопка "Переместить всю сеть" (toggle) в финальном окне
- **7.8** По умолчанию: одиночный элемент. По кнопке: вся сеть.
- **7.9** Тесты: линейная цепочка, ветвление, петля

### Алгоритм BuildGraph (BFS)

```
graph = new ConnectionGraph(root)
visited = {root}
queue = [root]
while queue:
    current = dequeue
    for connector in element.Connectors:
        if connected:
            for ref in AllRefs:
                neighborId = ref.Owner.Id
                if neighborId not in visited:
                    visited.add(neighborId)
                    queue.enqueue(neighborId)
                    graph.addEdge(...)
```

### Приёмка
- BuildGraph обходит линейную цепочку, ветвления, не зацикливается.
- "Переместить всю сеть" двигает всё как жёсткое тело.
- По умолчанию двигается только 1 элемент.

---

## Фаза 8 — Финальное окно PostProcessing (PipeConnectEditor)

**Цель:** Немодальное WPF-окно с полным контролем: превью, поворот, смена коннектора, выбор фитинга.

### Задачи

- **8.1** PipeConnectEditorView.xaml (немодальное)
- **8.2** PipeConnectEditorViewModel
- **8.3** Секция "Поворот": TextBox угла + кнопки + Ctrl+Left/Right. Шаг настраиваемый.
- **8.4** RotateElement вокруг оси Z коннектора на произвольный угол
- **8.5** Секция "Коннектор": DropDown свободных коннекторов + кнопка "Изменить"
- **8.6** Смена коннектора -> полный переалайн
- **8.7** Секция "Фитинги": ListView карточки. RadioButton выбор.
- **8.8** "Примерить" -> реальная вставка через Transaction (внутри TransactionGroup)
- **8.9** Смена фитинга: удалить старый Transaction, вставить новый Transaction
- **8.10** "Соединить" -> ConnectTo -> Assimilate -> закрыть
- **8.11** "Отмена" / ESC / закрытие -> RollBack
- **8.12** Toggle "Переместить всю сеть"
- **8.13** Все действия через PipeConnectExternalEvent
- **8.14** IDialogService для MiniTypeSelector поверх финального окна
- **8.15** Стилизация (SmartCon.UI)

### Layout окна

```
+--------------------------------------------+
| SmartCon - PipeConnect                [X]  |
+--------------------------------------------+
| Коннектор                                  |
| [v Connector 1 (Free) - Сварка] [Изменить] |
|                                            |
| Поворот                                   |
| [<-] [ 0.00 ] [->]       Шаг: [15 v]     |
|                                            |
| Фитинги                                   |
| o Без фитинга (прямое соединение)          |
| * СварнойШов DN50           [Примерить]    |
| o Переходник_С-Р DN50       [Примерить]    |
|                                            |
| [ ] Переместить всю сеть                  |
|                                            |
| [ Отмена ]                  [ Соединить ]  |
+--------------------------------------------+
```

### Механика TransactionGroup

```
1. Открыть TransactionGroup("PipeConnect")
2. Transaction("Align") -> перемещение/поворот -> Commit
3. Transaction("SetParameter") -> подбор размера -> Commit
4. Transaction("InsertFitting") -> вставка фитинга -> Commit
5. В окне:
   - "Повернуть" -> Transaction("Rotate") -> Commit
   - "Примерить" -> Transaction("ChangeFitting") -> delete old, insert new -> Commit
   - "Изменить коннектор" -> Transaction("ReAlign") -> Commit
6. "Соединить" -> Transaction("ConnectTo") -> Assimilate()
7. "Отмена" -> RollBack()
```

### Приёмка
- Окно немодальное, Revit доступен.
- Поворот работает в 3D.
- Смена коннектора переалигнивает.
- "Примерить" вставляет реальный фитинг.
- "Соединить" = одна Undo-запись.
- "Отмена" = чистая модель.
- Нет зависаний (все вызовы через ExternalEvent).

---

## Фаза 9 — Продвинутый функционал

### 9A — Алгоритм Дейкстры для цепочки фитингов

- FindShortestFittingPath(from, to) через граф типов
- Кейс: TYPE-1->TYPE-3 нет прямого правила, но есть 1->2 и 2->3 = цепочка из 2 фитингов

### 9B — Вставка трубы / арматуры

- Кнопки в финальном окне: "Вставить трубу" (длина), "Вставить арматуру" (семейство)
- Вставка между присоединёнными элементами

### 9C — Фильтры по DN в маппинге

- MinDN / MaxDN в FittingMappingRule
- UI в окне маппинга

### 9D — Инсталлятор

- WiX или Inno Setup
- Автоопределение версий Revit, копирование DLL+.addin, деинсталляция

---

## Граф зависимостей фаз

```
Фаза 0 -> Фаза 1 -> Фаза 2 --+--> Фаза 3 --+--> Фаза 5 --+
                               |              |              |
                               +--> Фаза 4 --+              +--> Фаза 8 -> Фаза 9
                               |                             |
                               +--> Фаза 7 -----------------+
                               |
              Фаза 1 ---------+--> Фаза 6 --------------------> Фаза 9
```

| Фаза | Название | Зависит от | Ключевой результат |
|---|---|---|---|
| 0 | Каркас | -- | Solution + Ribbon + DI |
| 1 | Фундамент | 0 | Модели + интерфейсы + сервисы |
| 2 | Базовый коннект | 1 | Клик-клик = ConnectTo |
| 3 | Типы коннекторов | 2 | MiniTypeSelector + маппинг + JSON |
| 4 | Параметры | 2 | Автоподбор размера |
| 5 | Фитинги | 3, 4 | Автовставка фитинга |
| 6 | FormulaSolver | 1 | Парсер формул Revit |
| 7 | Цепочки | 2 | Перемещение сети |
| 8 | Финальное окно | 5, 7 | PostProcessing UI |
| 9 | Продвинутое | 8, 6 | Дейкстра, труба, DN, инсталлятор |

---

## Инварианты (обязательны на ВСЕХ фазах)

| ID | Правило |
|---|---|
| I-01 | Revit API из WPF -- только через IExternalEventHandler |
| I-02 | Все числа в Core -- decimal feet |
| I-03 | Транзакции только через ITransactionService |
| I-04 | PipeConnect в TransactionGroup + Assimilate |
| I-05 | Не хранить Element/Connector между транзакциями, только ElementId |
| I-06 | UnitTypeId/ForgeTypeId (не DisplayUnitType) |
| I-07 | SmartConFailurePreprocessor на каждой Transaction |
| I-08 | Исключать ConnectorType.Curve из фильтров |
| I-09 | Core: запрет using Autodesk.Revit.DB и System.Windows |
| I-10 | MVVM: .xaml.cs только DataContext = viewModel |

---

## Открытые вопросы (уточнять по ходу разработки)

1. **BasisX снэп:** Точный алгоритм округления (15 vs 30 vs 45) -- протестировать на реальных кейсах и выбрать оптимальный шаг.
2. **EditFamily performance:** Открытие семейства для записи Description может быть медленным. Рассмотреть кеширование или batch-запись.
3. **Worksharing:** Если элемент в чужом рабочем наборе -- полагаемся на стандартное сообщение Revit через FailuresPreprocessor.
4. **Логирование:** Рассмотреть Serilog/NLog для диагностики если отладка через VS Attach станет недостаточной.
5. **FormulaSolver edge cases:** Формулы с циклическими зависимостями, деление на 0, нерешаемые уравнения.
