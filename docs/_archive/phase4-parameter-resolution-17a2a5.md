# Phase 4 — Подбор параметров (ResolvingParameters)

Автоматическое выравнивание диаметров при соединении: S4 подгоняет динамический элемент под радиус статического через LookupTable, прямой параметр или смену типоразмера — включая MiniFormulaSolver для простой арифметики и цепочек формул.

---

## Бизнес-кейсы (контекст для разработчика)

### Кейс 1: Два одинаковых диаметра — ничего не делаем
**Сценарий:** Инженер кликает по двум элементам DN50. Они уже совместимы.
**Ожидаемый результат:** S4 мгновенно переходит к ConnectTo, никаких изменений не происходит.

### Кейс 2: Труба DN40 подключается к фитингу DN50
**Сценарий:** Пользователь хочет соединить трубу (MEP Curve) с фитингом (FamilyInstance). Диаметры разные.
**Ожидаемый результат:**
- S4 определяет что динамический элемент — труба
- Меняет `RBS_PIPE_DIAMETER_PARAM` напрямую на DN50
- Труба автоматически адаптируется под размер фитинга
- ConnectTo проходит успешно

### Кейс 3: Муфта с LookupTable — точное совпадение DN50
**Сценарий:** Динамический элемент — муфта с встроенной таблицей размеров. Таблица содержит DN40, DN50, DN65, DN80. Нужен DN50.
**Ожидаемый результат:**
- S4 открывает семейство через EditFamily
- Находит какой FamilyParameter управляет CONNECTOR_RADIUS (например, "diameter")
- Парсит все формулы семейства, ищет `size_lookup(TableName, ..., diameter)`
- Экспортирует таблицу во временный CSV
- Находит строку где diameter = DN50
- Автоматически меняет типоразмер на DN50 через ChangeTypeId

### Кейс 4: Цепочка формул (radius = diameter / 2)
**Сценарий:** В семействе муфты: коннектор связан с параметром "radius", у "radius" формула "diameter / 2", в таблице участвует "diameter".
**Ожидаемый результат:**
- S4 определяет цепочку: коннектор ← radius ← diameter ← size_lookup
- MiniFormulaSolver вычисляет: для targetRadius = 25mm, нужный diameter = 50mm
- Ищет в таблице diameter = 50mm
- Устанавливает нужный типоразмер

### Кейс 5: Нет точного размера в таблице — ближайший DN40
**Сценарий:** В таблице есть DN40 и DN65, но нет DN50 (нужного размера).
**Ожидаемый результат:**
- S4 находит ближайший доступный: DN40
- Меняет элемент на DN40
- Устанавливает `NeedsAdapter = true`
- Показывает предупреждение: "Размер DN50 отсутствует в таблице. Будет выбран DN40, нужен переходник"
- Phase 5 (FittingMapper) увидит NeedsAdapter и вставит переходник

### Кейс 6: Смена типоразмера без таблицы
**Сценарий:** Динамический элемент — фитинг без size_lookup, но с несколькими типоразмерами в семействе (DN40, DN50, DN65).
**Ожидаемый результат:**
- S4 перебирает все FamilySymbol через SubTransaction
- Пробует ChangeTypeId на каждый типоразмер
- Проверяет радиус коннектора после смены
- Если нашёл DN50 — Commit, готово
- Если не нашёл точный — берёт ближайший + NeedsAdapter

### Кейс 7: Параметр экземпляра напрямую
**Сценарий:** В семействе коннектор связан с параметром экземпляра "MyDiameter" без формулы.
**Ожидаемый результат:**
- S4 определяет что параметр IsInstance и нет формулы
- Прямая запись: `parameter.Set(targetRadius)`

### Кейс 8: Сложная формула — не поддерживается
**Сценарий:** У параметра формула "if(diameter < 100, diameter / 2, diameter / 2 - 1)" или тригонометрия.
**Ожидаемый результат:**
- MiniFormulaSolver не справляется (нелинейная формула)
- S4 устанавливает `NeedsAdapter = true`
- Показывает предупреждение: "Сложная формула не поддерживается в Phase 4, будет вставлен переходник"
- Phase 6 (FormulaSolver) позже добавит полную поддержку

### Кейс 9: Полный провал — нет ни таблицы, ни параметра
**Сценарий:** Динамический элемент — какое-то нестандартное семейство без size_lookup и с непонятной структурой параметров.
**Ожидаемый результат:**
- S4 не может определить как менять размер
- Устанавливает `NeedsAdapter = true`
- Показывает предупреждение: "Не удалось подобрать размер. Будет вставлен переходник если есть в маппинге"
- **Операция НЕ отменяется** — продолжаем к S5
- Если в маппинге есть переходник для этой пары типов — он вставится
- Если нет — ConnectTo просто не сработает (разные диаметры), пользователь увидит стандартное сообщение Revit

### Кейс 10: Защита от ошибок — чужой рабочий набор
**Сценарий:** Попытка ChangeTypeId, но элемент в чужом рабочем наборе (worksharing).
**Ожидаемый результат:**
- SubTransaction открывается
- ChangeTypeId выбрасывает исключение
- SubTransaction.Rollback()
- S4 переходит к следующему типоразмеру
- Если все типоразмеры неудачны — NeedsAdapter = true

---

## Подтверждённые решения

| # | Решение |
|---|---------|
| D1 | S4 меняет **только динамический** элемент (статический — ориентир, не трогается) |
| D2 | Если найден ближайший (не точный) радиус → S4 **меняет** на ближайший + `NeedsAdapter = true` |
| D3 | Трубы (MEP Curve / Pipe) → менять `RBS_PIPE_DIAMETER_PARAM` напрямую, без EditFamily |
| D4 | S4 полностью провалился → `ShowWarning` + `NeedsAdapter = true` + **продолжить** (не отменять) |
| D5 | Смена типоразмера `ChangeTypeId` — **автоматически**, без диалога подтверждения |
| D6 | `SubTransaction` только как **страховка** от ошибки ChangeTypeId (не UI-превью) |
| D7 | Параметры с формулами: **MiniFormulaSolver** для простых `+,-,*,/`. Сложные (`if`, `trig`, таблицы) → NeedsAdapter (Phase 6) |
| D8 | **Анализ** (EditFamily, LookupTable) — **вне** TransactionGroup. **Запись** — внутри |
| D9 | Результат S4 → добавить поля в `PipeConnectionSession` |
| D10 | LookupTable: **парсим формулу** `size_lookup(Table, ИскомыйПараметр, "ЗначПоУмолчанию", ПарамЗапроса1, ПарамЗапроса2, ...)` → находим **позицию** управляющего параметра среди **параметров запроса** (4+ аргументы) → `colIndex = positionInQueryParams + 1` (первый столбец CSV = комментарии) |

---

## Компоненты Phase 4

### Шаг 1 — MiniFormulaSolver (`SmartCon.Core/Math/MiniFormulaSolver.cs`)

**Тип:** `internal static class` (не нужен DI, чистая математика Core)

```csharp
internal static class MiniFormulaSolver
{
    // Вычислить простое выражение: "diameter / 2", "DN * 2 + 1"
    // Поддержка: числа, переменные, +, -, *, /, унарный минус, скобки
    public static double Evaluate(string formula, IReadOnlyDictionary<string, double> variables);

    // Обратное решение линейной формулы: SolveFor("diameter / 2", "diameter", 25) → 50
    // Возвращает null если переменная не входит в формулу или формула нелинейна
    public static double? SolveFor(string formula, string variableName, double targetValue,
                                   IReadOnlyDictionary<string, double>? otherValues = null);

    // Парсинг size_lookup(TableName, ИскомыйПараметр, "ЗначПоУмолчанию", ПарамЗапроса1, ПарамЗапроса2, ...)
    // Возвращает: Имя таблицы, Искомый параметр (колонка результата), Список параметров запроса (4+ аргументы)
    public static (string TableName, string TargetParameter, IReadOnlyList<string> QueryParameters)? ParseSizeLookup(string formula);
}
```

**Реализация:**
- Tokenizer: разбить на токены (числа, идентификаторы, операторы, скобки)
- Recursive descent: `expr = term ((+|-) term)*`, `term = factor ((*|/) factor)*`, `factor = number | id | -factor | (expr)`
- `SolveFor`: подстановка с символическим вычислением. Если формула линейна по variableName (`a*x + b = T` → `x = (T-b)/a`) — решить алгебраически. Иначе — вернуть `null`
- `ParseSizeLookup`: regex + split по запятым — извлечь имя таблицы (arg0) и список параметров (arg2..N)

---

### Шаг 2 — Расширение `ParameterDependency` (`SmartCon.Core/Models/ParameterDependency.cs`)

Добавить поля для передачи инфо о цепочке формул:

```csharp
public sealed record ParameterDependency(
    BuiltInParameter? BuiltIn,       // для MEP Curve: RBS_PIPE_DIAMETER_PARAM
    string? SharedParamName,         // legacy
    string? Formula,                 // формула: connector_radius = f(directParam)
    bool IsInstance,                 // false = параметр типа
    string? DirectParamName,         // FamilyParameter напрямую связанный с CONNECTOR_RADIUS
    string? RootParamName            // "корневой" параметр если в directParam есть формула
                                     // (тот что фигурирует в size_lookup)
);
```

**Обновить `docs/domain/models.md`** после добавления полей.

---

### Шаг 3 — Расширение `PipeConnectionSession` (`SmartCon.Core/Models/PipeConnectionSession.cs`)

```csharp
// Добавить поля Phase 4:
public bool NeedsAdapter { get; set; }
public double OriginalDynamicRadius { get; set; }
public double ActualDynamicRadius { get; set; }

// В Reset():
NeedsAdapter = false;
OriginalDynamicRadius = 0;
ActualDynamicRadius = 0;
```

---

### Шаг 4 — `RevitParameterResolver` (`SmartCon.Revit/Parameters/RevitParameterResolver.cs`)

Реализует `IParameterResolver`.

#### `GetConnectorRadiusDependencies(doc, elementId, connectorIndex)`

**Для MEP Curve (Pipe/Duct/FlexPipe):**
- Проверить `element is MEPCurve or FlexPipe`
- Вернуть `[new ParameterDependency(BuiltIn: RBS_PIPE_DIAMETER_PARAM, IsInstance: true, DirectParamName: null, RootParamName: null)]`

**Для FamilyInstance:**
```
1. familyDoc = doc.EditFamily(element.Symbol.Family)
2. Найти ConnectorElement по connectorIndex (тот же индекс что в ConnectorProxy)
3. radiusParam = connectorElement.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS)
4. Итерировать familyDoc.FamilyManager.Parameters:
       foreach FamilyParameter fp:
           foreach Parameter assoc in fp.AssociatedParameters:
               if assoc.Id == radiusParam.Id && assoc.Element.Id == connectorElement.Id:
                   directParam = fp  // нашли!
5. Если directParam.Formula != null:
       // Пример: "diameter / 2" → ищем переменную "diameter" в FamilyManager
       rootVarName = MiniFormulaSolver.ExtractVariables(directParam.Formula).Single()
       rootParam = FamilyManager.Parameters.First(p => p.Definition.Name == rootVarName)
       return ParameterDependency(
           DirectParamName: directParam.Definition.Name,
           RootParamName: rootParam.Definition.Name,
           Formula: directParam.Formula,
           IsInstance: rootParam.IsInstance)
6. Иначе (нет формулы):
       return ParameterDependency(
           DirectParamName: directParam.Definition.Name,
           IsInstance: directParam.IsInstance)
7. familyDoc.Close(false)
```

#### `TrySetConnectorRadius(doc, elementId, connectorIndex, targetRadiusInternalUnits)`

```
1. deps = GetConnectorRadiusDependencies(doc, elementId, connectorIndex)  // повторный анализ
2. element = doc.GetElement(elementId)

Если element is MEPCurve:
    diameterFt = targetRadiusInternalUnits * 2
    element.get_Parameter(RBS_PIPE_DIAMETER_PARAM).Set(diameterFt)
    return true

Если dep.Formula == null (прямой параметр):
    param = element.LookupParameter(dep.DirectParamName)
    param.Set(targetRadiusInternalUnits)  // или с конвертацией единиц
    return true

Если dep.Formula != null (есть цепочка):
    paramValue = MiniFormulaSolver.SolveFor(dep.Formula, dep.RootParamName, targetRadiusInternalUnits)
    Если paramValue == null → return false  // сложная формула, не умеем
    param = element.LookupParameter(dep.RootParamName)
    param.Set(paramValue)
    return true

Если dep.IsInstance == false (параметр типа):
    return TryChangeTypeTo(doc, elementId, connectorIndex, targetRadiusInternalUnits)
```

#### `TryChangeTypeTo` (private helper)

```
1. family = (element as FamilyInstance).Symbol.Family
2. symbolIds = family.GetFamilySymbolIds()
3. Для каждого symbolId:
       using SubTransaction st = new SubTransaction(doc):
           st.Start()
           element.ChangeTypeId(symbolId)
           // перечитать коннектор
           newRadius = GetConnectorRadius(doc, elementId, connectorIndex)
           if |newRadius - targetRadius| < epsilon:
               st.Commit()
               return true
           else:
               st.RollBack()
4. // Не нашли точный — ищем ближайший
   bestSymbolId, bestRadius = найти минимум |radius - target|
   element.ChangeTypeId(bestSymbolId)
   return false  // false = нашли только ближайший (вызывающий выставит NeedsAdapter)
```

---

### Шаг 5 — `RevitLookupTableService` (`SmartCon.Revit/Parameters/RevitLookupTableService.cs`)

Реализует `ILookupTableService`.

#### Внутренний хелпер `BuildLookupContext(doc, elementId, connectorIndex)`

```
1. element = doc.GetElement(elementId)
2. Если MEP Curve → return null (нет LookupTable)
3. familyDoc = doc.EditFamily(element.Symbol.Family)
4. fstm = FamilySizeTableManager.GetFamilySizeTableManager(familyDoc, familyDoc.OwnerFamily.Id)
5. Если fstm.NumberOfSizeTables == 0 → familyDoc.Close(false); return null
6. deps = RevitParameterResolver.Analyze(familyDoc, connectorIndex)  // внутренний метод
7. searchParamName = deps.RootParamName ?? deps.DirectParamName
8. Для каждого tableName в fstm.GetAllSizeTableNames():
       // Проверить: есть ли формулы вида size_lookup(tableName, ..., searchParamName, ...)
       foreach FamilyParameter fp in familyDoc.FamilyManager.Parameters:
           if fp.Formula != null:
               parsed = MiniFormulaSolver.ParseSizeLookup(fp.Formula)
               // parsed.QueryParameters = [ПарамЗапроса1, ПарамЗапроса2, ...] — аргументы с 4-й позиции
               if parsed?.TableName == tableName && parsed.QueryParameters.Contains(searchParamName):
                   // Находим позицию в списке ПАРАМЕТРОВ ЗАПРОСА (не считая Table, Target, Default)
                   queryParamIndex = parsed.QueryParameters.IndexOf(searchParamName)
                   // colIndex = queryParamIndex + 1, потому что col[0] = комментарии в CSV
                   colIndex = queryParamIndex + 1
                   // Экспортировать таблицу во временный файл
                   tempPath = Path.GetTempFileName()
                   fstm.ExportSizeTable(tableName, tempPath)
                   csv = File.ReadAllLines(tempPath)
                   File.Delete(tempPath)
                   // Первый столбец CSV = комментарии, данные с colIndex+1
                   return new LookupContext(familyDoc, tableName, csv, colIndex + 1, deps)
9. familyDoc.Close(false)
   return null
```

#### `ConnectorRadiusExistsInTable(doc, familySymbolId, radiusInternalUnits)`

```
1. ctx = BuildLookupContext(doc, ...)
2. Если ctx == null → return false
3. Извлечь уникальные значения из colIndex столбца CSV
4. Конвертировать targetRadius (feet) в единицы колонки (мм):
       valueMm = UnitUtils.ConvertFromInternalUnits(targetRadius, UnitTypeId.Millimeters)
       Если есть deps.Formula: valueMm = применить обратную формулу
5. return значения CSV содержат valueMm (с допуском 0.01 мм)
```

#### `GetNearestAvailableRadius(doc, familySymbolId, targetRadiusInternalUnits)`

```
1. ctx = BuildLookupContext(...)
2. Если ctx == null → return targetRadiusInternalUnits  // нет таблицы, вернуть как есть
3. Извлечь все уникальные числовые значения колонки
4. Конвертировать в internal units (feet * радиус)
5. Найти значение с минимальным |value - target|
6. return nearestRadius (internal units)
```

---

### Шаг 6 — `FamilyParameterAnalyzer` (`SmartCon.Revit/Parameters/FamilyParameterAnalyzer.cs`)

**Внутренний** (не интерфейс) хелпер, используется и `RevitParameterResolver`, и `RevitLookupTableService` для исключения дублирования логики EditFamily.

```csharp
internal static class FamilyParameterAnalyzer
{
    // Открыть familyDoc и вернуть FamilyParameter управляющий CONNECTOR_RADIUS у connector[index]
    // + формулу цепочки если есть
    // familyDoc НЕ закрывать здесь — закрывает вызывающий
    internal static (string? DirectParamName, string? RootParamName, string? Formula, bool IsInstance)
        AnalyzeConnectorRadiusParam(Document familyDoc, int connectorIndex);
}
```

---

### Шаг 7 — Интеграция S4 в `PipeConnectCommand.cs`

#### Структура `ParameterResolutionPlan` (приватный record внутри команды)

```csharp
private sealed record ParameterResolutionPlan(
    bool Skip,                   // радиусы совпадают, ничего делать не надо
    double TargetRadius,         // целевой радиус (internal units)
    bool ExpectNeedsAdapter,     // нашли только ближайший, не точный
    string? WarningMessage       // null = нет предупреждения
);
```

#### Полный поток S4 в `Execute()`:

```
// ── PHASE S4: ANALYSIS (до TransactionGroup, EditFamily разрешён) ──
ParameterResolutionPlan plan = BuildResolutionPlan(
    doc, dynamicProxy, staticProxy.Radius,
    paramResolver, lookupTableSvc, dialogSvc);

bool connectSucceeded = false;

txService.RunInTransactionGroup("PipeConnect — Соединить", groupDoc =>
{
    // Transaction 1: Align
    txService.RunInTransaction("Align", ...);

    // Transaction 2: SetParam (Phase 4)
    if (!plan.Skip)
    {
        txService.RunInTransaction("SetParam", paramDoc =>
        {
            bool success = paramResolver.TrySetConnectorRadius(
                paramDoc, dynamicProxy.OwnerElementId,
                dynamicProxy.ConnectorIndex, plan.TargetRadius);

            if (!success || plan.ExpectNeedsAdapter)
                session.NeedsAdapter = true;

            session.OriginalDynamicRadius = dynamicProxy.Radius;
            session.ActualDynamicRadius = plan.TargetRadius;
        });
    }

    // Transaction 3: Connect
    txService.RunInTransaction("Connect", ...);
});

if (plan.WarningMessage != null)
    dialogSvc.ShowWarning("SmartCon", plan.WarningMessage);
```

#### `BuildResolutionPlan` (приватный метод):

```
1. Если |static.Radius - dynamic.Radius| < epsilon → return Plan(Skip: true)

2. Если dynamic element is MEP Curve:
       return Plan(TargetRadius: static.Radius)  // напрямую через RBS_PIPE_DIAMETER_PARAM

3. deps = paramResolver.GetConnectorRadiusDependencies(doc, dynamicId, connIdx)

4. Если нет deps (пустой список) AND нет LookupTable:
       return Plan(Skip: false, TargetRadius: static.Radius,
                   WarningMessage: "Не удалось определить параметр размера. Будет вставлен переходник.")
                   ExpectNeedsAdapter: true)

5. Если lookupTableSvc.ConnectorRadiusExistsInTable(static.Radius):
       return Plan(TargetRadius: static.Radius)

6. Если LookupTable есть но нет точного совпадения:
       nearest = lookupTableSvc.GetNearestAvailableRadius(static.Radius)
       return Plan(TargetRadius: nearest, ExpectNeedsAdapter: true,
                   WarningMessage: $"Размер DN{staticMm} отсутствует в таблице. Будет выбран DN{nearestMm}, нужен переходник.")

7. Если dep.Formula != null:
       val = MiniFormulaSolver.SolveFor(dep.Formula, dep.RootParamName, static.Radius)
       Если val == null → return Plan с NeedsAdapter + Warning ("формула не поддерживается в Phase 4")
       return Plan(TargetRadius: static.Radius)  // TrySetConnectorRadius вычислит val сам

8. Если dep.IsInstance (параметр экземпляра без формулы):
       return Plan(TargetRadius: static.Radius)

9. Если !dep.IsInstance (параметр типа):
       return Plan(TargetRadius: static.Radius)  // TryChangeTypeTo разберётся сам

10. Fallback: return Plan(Skip: false, ExpectNeedsAdapter: true,
                          WarningMessage: "Не удалось подобрать размер.")
```

---

### Шаг 8 — `ServiceRegistrar.cs` (`SmartCon.App/DI/ServiceRegistrar.cs`)

Добавить регистрации:
```csharp
services.AddSingleton<IParameterResolver, RevitParameterResolver>();
services.AddSingleton<ILookupTableService, RevitLookupTableService>();
```

---

### Шаг 9 — Тесты

#### `SmartCon.Tests/Core/Math/MiniFormulaSolverTests.cs` (20+ тестов)

| Группа | Кейсы |
|--------|-------|
| Evaluate | `"2 + 3" = 5`, `"x * 2"` с x=10 → 20, `"(a + b) / 2"`, `"-x"` |
| SolveFor | `"x / 2"` target=25 → 50, `"x * 2 + 1"` target=11 → 5, `"x + 0"` target=5 → 5 |
| SolveFor (null) | `"x * x"` (нелинейно) → null, переменная не в формуле → null |
| ParseSizeLookup | `"size_lookup(Table1, 50, DN, PN)"` → `("Table1", ["DN","PN"])` |
| ParseSizeLookup | не size_lookup строка → null |
| Единицы | `"25 / 2 + 0"` без переменных → 12.5 |

#### `SmartCon.Tests/Core/Services/ParameterResolutionFlowTests.cs` (12+ тестов)

Мокировать `IParameterResolver` и `ILookupTableService` через Moq:

| Кейс | Мок | Ожидание |
|------|-----|----------|
| Радиусы совпадают | — | Plan.Skip = true |
| LookupTable есть, размер точный | `ConnectorRadiusExistsInTable = true` | Plan.TargetRadius = static.Radius, NeedsAdapter = false |
| LookupTable есть, нет точного | `Exists=false`, `GetNearest = nearest` | NeedsAdapter = true, warning |
| LookupTable нет, IsInstance=true, нет формулы | deps.Formula = null | Plan.TargetRadius = static.Radius |
| LookupTable нет, IsInstance=true, есть формула | deps.Formula = "x/2" | SolveFor вычислен |
| Параметр типа | deps.IsInstance = false | TrySetConnectorRadius вызван |
| S4 полностью провалился | все возвращают false | NeedsAdapter=true + Warning |
| MEP Curve | element is MEPCurve | прямая запись без EditFamily |

---

## Файлы к созданию/изменению

### Новые файлы

| Файл | Тип | Содержимое |
|------|-----|-----------|
| `SmartCon.Core/Math/MiniFormulaSolver.cs` | NEW | static class, чистая математика |
| `SmartCon.Revit/Parameters/FamilyParameterAnalyzer.cs` | NEW | internal static helper |
| `SmartCon.Revit/Parameters/RevitParameterResolver.cs` | NEW | IParameterResolver impl |
| `SmartCon.Revit/Parameters/RevitLookupTableService.cs` | NEW | ILookupTableService impl |
| `SmartCon.Tests/Core/Math/MiniFormulaSolverTests.cs` | NEW | 20+ тестов |
| `SmartCon.Tests/Core/Services/ParameterResolutionFlowTests.cs` | NEW | 12+ тестов Moq |

### Изменяемые файлы

| Файл | Что изменить |
|------|-------------|
| `SmartCon.Core/Models/ParameterDependency.cs` | добавить `DirectParamName`, `RootParamName` |
| `SmartCon.Core/Models/PipeConnectionSession.cs` | добавить `NeedsAdapter`, `OriginalDynamicRadius`, `ActualDynamicRadius` |
| `SmartCon.PipeConnect/Commands/PipeConnectCommand.cs` | добавить S4 шаги (анализ + action) |
| `SmartCon.App/DI/ServiceRegistrar.cs` | зарегистрировать IParameterResolver, ILookupTableService |
| `docs/domain/models.md` | обновить ParameterDependency, PipeConnectionSession |
| `docs/pipeconnect/algorithms.md` | уточнить алгоритм S4 с MiniFormulaSolver |
| `docs/roadmap.md` | Phase 4 → В работе / Готов |
| `docs/README.md` | обновить статус и кол-во тестов |

---

## Алгоритм подбора (полная схема S4)

```
S4: ResolvingParameters
│
├── static.Radius == dynamic.Radius?
│       YES → пропустить S4
│
├── dynamic элемент = MEP Curve (Pipe)?
│       YES → RBS_PIPE_DIAMETER_PARAM.Set(radius * 2) → готово
│
├── (вне TransactionGroup) GetConnectorRadiusDependencies
│   └── EditFamily → найти FamilyParameter → следить формулу
│
├── ConnectorRadiusExistsInTable?
│       YES → plan.TargetRadius = static.Radius
│       NO → plan.TargetRadius = GetNearestAvailableRadius
│              NeedsAdapter = true
│              ShowWarning("нет точного размера")
│
├── dep.IsInstance && dep.Formula == null → прямая запись
│
├── dep.IsInstance && dep.Formula != null (простая)
│       MiniFormulaSolver.SolveFor → вычислить paramValue
│       dep.IsInstance && dep.Formula != null (сложная) → NeedsAdapter (Phase 6)
│
├── dep.IsInstance == false (тип)
│       SubTransaction → ChangeTypeId перебором
│       точный → Commit
│       ближайший → NeedsAdapter = true
│
└── ничего не получилось → ShowWarning + NeedsAdapter = true + continue
```

---

## Критерии приёмки (Acceptance)

- [ ] Два элемента с разными диаметрами → S4 меняет динамический автоматически
- [ ] Труба (Pipe) меняет диаметр через `RBS_PIPE_DIAMETER_PARAM`
- [ ] LookupTable парсится: `ParseSizeLookup` + `ExportSizeTable` + CSV чтение
- [ ] LookupTable: парсим формулу `size_lookup(TableName, ИскомыйПараметр, "ЗначПоУмолчанию", ПарамЗапроса1, ПарамЗапроса2, ...)` через `ParseSizeLookup`, находим позицию управляющего параметра среди **параметров запроса** (4+ аргументы), `colIndex = positionInQueryParams + 1` (первый столбец CSV зарезервирован под комментарии)
- [ ] Цепочка формул (`radius = diameter / 2`) разрешается `MiniFormulaSolver`
- [ ] Смена типоразмера защищена `SubTransaction` (rollback при ошибке)
- [ ] Ближайший размер → `NeedsAdapter = true` + предупреждение пользователю
- [ ] Полный провал S4 → `ShowWarning` + `NeedsAdapter = true` + операция **не отменяется**
- [ ] Анализ LookupTable выполняется **до** открытия `TransactionGroup`
- [ ] Одна Undo-запись (TransactionGroup.Assimilate)
- [ ] 0 ошибок компиляции
- [ ] Все тесты pass (цель: 120 → 152+ тестов)
- [ ] `docs/domain/models.md` и `docs/pipeconnect/algorithms.md` обновлены

---

## Технические риски и ограничения

| Риск | Митигация |
|------|-----------|
| `EditFamily` медленно для больших семейств | Кешировать результат `GetConnectorRadiusDependencies` в рамках одной операции |
| `FamilySizeTable.ExportSizeTable` пишет во временный файл (I/O) | Писать в `Path.GetTempFileName()`, удалять сразу после чтения |
| Цепочка формул глубже 1 уровня (radius → factor → DN) | В Phase 4 поддерживаем только 1 уровень. Глубже 1 → NeedsAdapter (Phase 6) |
| Тип `ConnectorElement` индексируется по-разному в project vs family doc | Использовать устойчивый индекс из `ConnectorProxy.ConnectorIndex` (Phase 2 подтверждено) |
| `SubTransaction` внутри TransactionGroup — разрешено в Revit API | Да, SubTransaction работает внутри открытой Transaction |

---

*Создан: 2026-03-31 | Статус: Ожидает старта реализации*
