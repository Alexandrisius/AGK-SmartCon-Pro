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

### DN-компенсация на уровнях сети (ADR-053, ревизия 2026-07-20)

При обработке уровней (`ChainOperationHandler.AdjustElementSize`) несовпадение
радиусов с родителем разрешается иерархией стратегий:

```
1. TRANSITION — элемент сам становится переходным (тройник DN25×DN25 → DN20×DN25).
   TransitionSizeMatcher по prefetch-нутым GetAvailableFamilySizes:
   target-порт совпадает точно, остальные порты меняются минимально (идеально 0).
   Применение: ApplyQueryParamsIfExists → иначе per-connector TrySetConnectorRadius.
   Опции со сменой FamilySymbol исключены.
2. REDUCER — редьюсер из маппинга CTC (NetworkMover.InsertReducer),
   элемент и сеть не трогаем (для FamilyInstance — до resize).
3. RESIZE — классический resize; каскад останавливается на первом
   DN-поглощающем элементе ниже (зеркало pipe length absorption, §7).
```

`AdjustRelatedFamilyConnectors` трогает только порты с **общим** DN-параметром
(`DnParamsShared` — сравнение имён из `GetConnectorRadiusDependencies`).
Независимые порты сохраняют DN — точечный фикс #146.

Prefetch конфигураций — в snapshot-фазе `IncrementLevel` (вне транзакции:
`GetAvailableFamilySizes` требует `IsModifiable == false` из-за EditFamily).

Known limitation: семейства без lookup/типов с nested-IF формулами, ожидающими
конкретные DN — см. ADR-053.

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

## 5. Алгоритм подбора цепочки фитингов (FittingMapper / IFittingChainResolver)

**Файлы:** `SmartCon.Core/Services/Implementation/FittingMapper.cs`, `SmartCon.Core/Services/Interfaces/IFittingChainResolver.cs`  
**ADR:** [ADR-010](../adr/010-fitting-chain-resolver.md)

Граф строится из `FittingMappingRule`:
- **Узел** = `ConnectionTypeCode`
- **Ребро** = правило маппинга (`FromType -> ToType`)
- **Вес** = Priority правила

```
rules = IFittingMapper.GetMappings(static.TypeCode, dynamic.TypeCode)

Если rules пуст:
   --> path = IFittingChainResolver.FindShortestPath(static.TypeCode, dynamic.TypeCode)
   --> Если путь найден: rules = цепочка правил
   --> Если нет: предупреждение, ProposedFittings = []
```

**Кейс:** TYPE-1 → TYPE-3 прямого правила нет, но есть TYPE-1→TYPE-2 (Priority=1) и TYPE-2→TYPE-3 (Priority=2). Результат: цепочка из 2 фитингов, суммарный вес = 3.

Для каждого правила в цепочке:
- `IsDirectConnect` + `FittingFamilies` пуст → прямое соединение
- `IsDirectConnect` + `FittingFamilies` не пуст → фильтрация по размерам, автовыбор по Priority
- `!IsDirectConnect` → обязателен фитинг-переходник, фильтрация + автовыбор

---

## 6. FormulaSolver: архитектура парсера

**Файл:** `SmartCon.Core/Math/FormulaEngine/Solver/FormulaSolver.cs`  
**ADR:** [ADR-005](../adr/005-formula-solver-ast.md)

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

---

## 7. Гашение смещения сети длиной трубы (per-level absorption)

**Файл:** `SmartCon.Core/Math/PipeLengthAbsorber.cs` (pure math, Vec3)
**Интеграция:** `SmartCon.PipeConnect/Services/ChainOperationHandler.cs` (`TryAlignPipeByLength`)
**ADR:** [ADR-052](../adr/052-pipe-length-displacement-absorption.md)

### Проблема

При подключении уровня сети (кнопки `+` / «Подключить всё») элемент отсоединялся,
сдвигался как жёсткое тело на вектор выравнивания `v` и переподключался. Смещение
протаскивалось по всем уровням — вся система (например, отопление здания) сдвигалась
на `v`, хотя физически нужно было только скорректировать длину одной трубы.

### Идея (per-level, ревизия 2026-07-20)

Смещение распространяется по уровням классически — по одному элементу за инкремент.
Когда инкремент доходит до элемента, который **сам является прямой трубой**, и его
выравнивание — чистая трансляция, труба **меняет длину вместо перемещения**:
ближний к родителю конец `LocationCurve` следует за `v`, дальний получает только
непоглощённый остаток. При полном поглощении propagation останавливается —
downstream-уровни получают нулевой offset. Вперёд и назад — строго поэлементно.

> Eager-модель (обход поддерева вперёд) отвергнута после полевых тестов: один
> уровень двигал весь узел, электрические коннекторы терялись, уровни 12–13
> оставались пустыми. См. ADR-052 §"Почему eager-модель отвергнута".

### Предусловия

Гашение применяется, только когда элемент — MEPCurve с `LocationCurve` типа `Line`
и выравнивание — **чистая трансляция** (`BasisZRotation == null && BasisXSnap == null
&& !IsZero(InitialOffset)`). Иначе → классический rigid align.

### Алгоритм (PipeLengthAbsorber.Compute)

```
axis     = normalize(far - near)      // near = конец кривой ближе к коннектору родителя
axial    = dot(v, axis)
absorbed = axial > 0
    ? min(axial, max(0, length - minPipeLength))   // укорочение ограничено минимумом
    : axial                                        // удлинение без ограничений
nearDelta = v                          // ближний конец → к родителю (точное совпадение)
farDelta  = v - axis * absorbed        // дальний конец: 0 при полном поглощении
lc.Curve = Line.CreateBound(p0 + StartDelta, p1 + EndDelta)
```

### Правила (бизнес-решения)

1. **Минимальная длина трубы 100 мм** (`PipeAbsorption.MinPipeLengthMm` →
   `MinPipeLengthFt = 100 / 304.8`). Revit падает ниже ~2.5 мм (1/10"), но монтажно
   короткие вставки нежелательны. Труба уже короче 100 мм не укорачивается вообще
   (чистая трансляция, весь offset уходит дальше).
2. **Частичное поглощение:** запаса не хватает → труба гасит до 100 мм, остаток
   получает следующий уровень (возможно, следующая труба доберёт его на своём уровне).
3. **Перпендикулярная труба** математически вырождается в трансляцию (`absorbed ≈ 0`).
4. Обработка только своего элемента — электрика и прочие домены не затрагиваются.

### Применение (ChainOperationHandler.ProcessIncrementElement)

1. `DisconnectElementConnections` → `AdjustElementSize` (как раньше).
2. `TryAlignPipeByLength`: вычислить `ConnectorAligner.ComputeAlignment`; если
   трансляция и элемент — прямая труба: `PipeLengthAbsorber.Compute` →
   `lc.Curve = Line.CreateBound(...)` → `doc.Regenerate()`. Иначе — `AlignElement`.
3. `ReconnectIncrementElement` — ближний конец совпадает с родителем точно.
4. Дальний конец трубы переподключается на уровне downstream-соседа обычным
   потоком (offset = остаток или 0).

### Откат (DecrementLevel)

Без изменений: восстанавливаются только элементы текущего уровня из снапшотов
(кривая трубы восстанавливается через `RestoreMepCurve`). Полная симметрия с
increment: один уровень = один элемент в обе стороны.

### FlexPipe (addendum 2026-07-20)

Гибкая труба гасит смещение через сеттер `FlexPipe.Points` (НЕ `LocationCurve.Curve` —
присвоение HermiteSpline для flex-элементов бросает исключение, Autodesk forum
10671223). Меняется только концевая точка со стороны родителя на полный offset
(flex гнётся в любую сторону), промежуточные точки сохраняются verbatim
(`PipeLengthAbsorber.ComputeFlexPath`). Ограничение: длина ломаного пути после
изменения ≥ 100 мм, иначе rigid fallback. Снапшот хранит `FlexPoints` целиком —
откат восстанавливает форму полностью.

### Запечатывание и авто-пропуск мёртвых уровней (addendum 2026-07-20)

**Запечатывание (seal):** после каждого инкремента `TrySealQuietChain` проверяет,
осталась ли работа: все boundary-дети следующего уровня с нулевым offset, без
поворотов и с совпадающим радиусом + все глубокие piping-рёбра с совпадающими
радиусами. Если тихо — одна транзакция `Tx_ChainSeal` переподключает границу,
`+` и «Подключить всё» блокируются (`_chainSealed`), сеть дальше не трогается.
Откат снимает флаг.

**Авто-пропуск (fallback):** `IncrementLevel` возвращает `anyWork`; VM хранит
флаги `_levelDidWork` и при `+`/`−` авто-пропускает уровни без изменений
(обработка выполняется — нужна для переподключения), статус «пропущено без
изменений: N». Уровни с resize/reducer никогда не пропускаются. Эффективный
максимум уровней не предсказывается заранее — зависит от компенсаций и диаметров.
