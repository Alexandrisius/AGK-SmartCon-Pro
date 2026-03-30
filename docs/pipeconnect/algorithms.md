# PipeConnect: Алгоритмы

> Загружать: при реализации алгоритмов выравнивания, подбора параметров, фитингов, цепочек.

---

## 1. Алгоритм выравнивания (ConnectorAligner)

**Файл:** `SmartCon.Core/Math/ConnectorAligner.cs`
**Вход:** `staticConnector: ConnectorProxy`, `dynamicConnector: ConnectorProxy`
**Выход:** Вектор смещения + ось/угол поворота (или набор Transform-операций)

### Шаги

**Шаг 1 — Перемещение в единую точку:**
```
offset = static.Origin - dynamic.Origin
MoveElement(doc, dynamicElementId, offset)
```
После этого Origin-ы совпадают.

**Шаг 2 — Поворот BasisZ (антипараллельность):**
Целевой вектор: `targetZ = -static.BasisZ` (коннекторы должны смотреть друг на друга).

```
cosAngle = dynamic.BasisZ . targetZ
angle = acos(cosAngle)

Если angle < epsilon:
    --> BasisZ уже антипараллельны, пропускаем

Если |angle - PI| < epsilon:
    --> BasisZ сонаправлены (коллинеарны в одну сторону)
    --> Разворот на 180 вокруг любой перпендикулярной оси
    --> axis = FindPerpendicularAxis(dynamic.BasisZ)

Иначе:
    --> axis = Normalize(dynamic.BasisZ x targetZ)
    --> RotateElement(doc, dynamicElementId, Line(origin, origin + axis), angle)
```

**Шаг 3 — Снэп BasisX к «красивому» углу:**
После выравнивания Z вычисляем угол между BasisX в плоскости коннектора:
```
currentAngle = AngleBetweenInPlane(dynamic.BasisX, static.BasisX, planeNormal: static.BasisZ)
snappedAngle = RoundToNearest(currentAngle, step: 15 градусов)
deltaAngle = snappedAngle - currentAngle

Если |deltaAngle| > epsilon:
    RotateElement(doc, dynamicElementId, Line(origin, origin + static.BasisZ), deltaAngle)
```

Шаг 15° покрывает все «красивые» углы: 0°, 15°, 30°, 45°, 60°, 75°, 90°...
Пользователь видит чистый угол и может довернуть вручную в финальном окне.

**Шаг 4 — Коррекция позиции:**
После поворота Origin динамического коннектора мог сместиться.
```
Перечитываем коннектор: newDynamic = GetConnector(doc, dynamicElementId, connectorIndex)
correction = static.Origin - newDynamic.Origin
Если |correction| > epsilon:
    MoveElement(doc, dynamicElementId, correction)
```

### Применение к цепочке

При `MoveEntireChain == true` все операции (Move, Rotate) применяются к каждому `ElementId` в `ConnectionGraph.Nodes`.

---

## 2. Алгоритм подбора параметров (S4)

**Вход:** `staticConnector`, `dynamicConnector` (после выравнивания)
**Цель:** Сделать `dynamic.Radius == static.Radius`

### Приоритет проверок

```
1. Радиусы совпадают?
   Да --> пропустить, перейти к S5

2. Получить зависимости:
   deps = IParameterResolver.GetConnectorRadiusDependencies(doc, dynamicElementId, connectorIndex)

3. Проверить LookupTable:
   Если ConnectorRadiusExistsInTable(doc, symbolId, static.Radius):
       --> TrySetConnectorRadius(doc, elemId, connIndex, static.Radius) --> S5
   Иначе:
       nearestRadius = GetNearestAvailableRadius(doc, symbolId, static.Radius)
       --> Запомнить nearestRadius, пометить "нужен переходник"

4. Параметр экземпляра (dep.IsInstance == true):
   Если dep.Formula == null:
       --> Прямая запись значения в параметр
   Если dep.Formula != null:
       --> IFormulaSolver.SolveFor(dep.Formula, paramName, targetRadius, otherValues)
       --> Округлить до 6 знаков
       --> Записать

5. Параметр типа (dep.IsInstance == false):
   --> Перебрать все FamilySymbol данного семейства
   --> Найти тот, где радиус коннектора == static.Radius
   --> Найден: doc.GetElement(elemId).ChangeTypeId(newSymbolId)
   --> Не найден: использовать ближайший + пометить "нужен переходник"

6. Ничего не помогло:
   --> Уведомление пользователю через IDialogService
```

### Важно

- Перед изменением параметра типа — `SubTransaction` для проверки (preview), затем Commit или Rollback.
- Результат `SolveFor()` округляется до 6 знаков decimal feet перед записью.

---

## 3. Алгоритм подбора фитингов (S5)

**Вход:** `staticConnector.ConnectionTypeCode`, `dynamicConnector.ConnectionTypeCode`
**Выход:** `ProposedFittings` в сессии

### Логика

```
1. rules = IFittingMapper.GetMappings(static.TypeCode, dynamic.TypeCode)

2. Если rules пуст:
   --> FindShortestFittingPath(static.TypeCode, dynamic.TypeCode)  (Дейкстра)
   --> Если путь найден: rules = цепочка правил
   --> Если нет: предупреждение, ProposedFittings = []

3. Для каждого rule в rules:
   Если rule.IsDirectConnect и rule.FittingFamilies пуст:
       --> Прямое соединение, ProposedFittings = []

   Если rule.IsDirectConnect и rule.FittingFamilies не пуст:
       --> Фильтруем семейства по совместимости размеров коннекторов
       --> 1 семейство: автовыбор
       --> Несколько: первый по Priority

   Если !rule.IsDirectConnect:
       --> Обязателен фитинг-переходник
       --> Фильтруем по размерам
       --> Автовыбор первого по Priority
```

### Фильтрация по размерам коннекторов фитинга

Для каждого FamilySymbol из правила:
1. Загрузить семейство, получить коннекторы
2. Проверить: есть ли коннектор с радиусом == static.Radius и коннектор с радиусом == dynamic.Radius
3. Если нет подходящих типоразмеров — исключить из списка

### Вставка фитинга

```
1. symbol = FindFamilySymbol(doc, familyName, symbolName)
2. instance = doc.Create.NewFamilyInstance(origin, symbol, StructuralType.NonStructural)
3. Выровнять коннекторы фитинга:
   fittingConn1 --> static коннектор (ConnectorAligner)
   fittingConn2 --> dynamic коннектор
4. НЕ вызывать ConnectTo() здесь — это будет в S6 при нажатии "Соединить"
```

---

## 4. Алгоритм обхода цепочки (BuildGraph / BFS)

**Файл:** `SmartCon.Revit/Selection/ElementChainIterator.cs`

```
BuildGraph(doc, startElementId, stopAtElements = null):
    graph = new ConnectionGraph(root = startElementId)
    visited = HashSet<ElementId> { startElementId }
    queue = Queue<ElementId> { startElementId }

    while queue.Count > 0:
        currentId = queue.Dequeue()
        element = doc.GetElement(currentId)
        connectorManager = GetConnectorManager(element)

        for each connector in connectorManager.Connectors:
            if connector.IsConnected == false: continue
            if connector.ConnectorType == ConnectorType.Curve: continue  // I-08

            for each ref in connector.AllRefs:
                neighborId = ref.Owner.Id
                if neighborId == currentId: continue
                if visited.Contains(neighborId): continue
                if stopAtElements?.Contains(neighborId) == true: continue

                visited.Add(neighborId)
                queue.Enqueue(neighborId)
                graph.AddEdge(currentId, connector.Id, neighborId, ref.Id)

    return graph
```

### Особенности
- **Тройники/крестовины:** все ветки включаются в граф (BFS обходит все направления)
- **Защита от циклов:** `visited` HashSet
- **Ограничений на глубину нет**
- **ConnectorType.Curve** исключается (инвариант I-08)

---

## 5. Алгоритм Дейкстры для цепочки фитингов (PathfinderService)

**Файл:** `SmartCon.Core/Services/Implementation/PathfinderService.cs`
**Фаза:** 9A (продвинутый функционал)

Граф строится из `FittingMappingRule`:
- **Узел** = `ConnectionTypeCode`
- **Ребро** = правило маппинга (FromType -> ToType)
- **Вес** = Priority правила

```
FindShortestFittingPath(from: ConnectionTypeCode, to: ConnectionTypeCode):
    Стандартный Дейкстра на графе типов
    Возвращает: List<FittingMappingRule> — минимальная цепочка переходников
```

**Кейс:** TYPE-1 -> TYPE-3 прямого правила нет, но есть TYPE-1->TYPE-2 (Priority=1) и TYPE-2->TYPE-3 (Priority=2). Результат: цепочка из 2 фитингов, суммарный вес = 3.

---

## 6. FormulaSolver: архитектура парсера

**Файл:** `SmartCon.Core/Services/Implementation/FormulaSolver.cs`
**Фаза:** 6

### Pipeline

```
Строка формулы --> [Tokenizer] --> Token[] --> [Parser] --> AST (дерево)
                                                             |
                                              +--------------+--------------+
                                              |              |              |
                                         Evaluate()    SolveFor()    ParseSizeLookup()
```

### Поддерживаемые конструкции

- **Операторы:** `+`, `-`, `*`, `/`, `^`, `%`
- **Сравнения:** `<`, `>`, `<=`, `>=`, `=`, `<>`
- **Логика:** `and()`, `or()`, `not()`
- **Ветвления:** `if(condition, trueValue, falseValue)`
- **Тригонометрия:** `sin()`, `cos()`, `tan()`, `asin()`, `acos()`, `atan()`
- **Математика:** `abs()`, `sqrt()`, `round()`, `roundup()`, `rounddown()`, `min()`, `max()`
- **Константы:** `pi`, `e`
- **Единицы:** `mm`, `m`, `ft`, `in` (конвертируются в Internal Units при парсинге)

### SolveFor: стратегия обратного решения

1. **Линейные формулы** (x * a + b): алгебраическая инверсия AST
2. **Сложные** (if, trig): метод бисекции на интервале допустимых значений
3. Округление результата до 6 знаков decimal feet
