# План фикса багов парсинга размеров динамических семейств

> Дата: 2026-04-09
> Статус: Требуется реализация
> Контекст: Передано от предыдущего агента

---

## Контекст проделанной работы

В рамках предыдущих итераций реализовано:
1. Новая модель `FamilySizeOption` с `AllConnectorRadii` (радиусы всех коннекторов)
2. `FamilySizeFormatter` (Core) — форматирование DisplayName вида `"DN 65 × DN 50"`
3. `GetAllSizeRows()` в `RevitLookupTableService` — извлечение строк CSV с маппингом столбцов на коннекторы
4. `GetAvailableFamilySizes()` в `RevitDynamicSizeResolver` — объединение LookupTable + FamilySymbol
5. Двухпроходная стратегия в `BuildResolutionPlan` (с/без constraints)
6. `ChangeDynamicSize` — установка радиусов ВСЕХ коннекторов из `AllConnectorRadii`
7. `TableColumnInfo.ConnectorIndices: List<int>` — один столбец CSV → несколько коннекторов

Все 491 тестов проходят. Сборка без ошибок.

---

## Три оставшихся бага

### Баг 1: Упрощение `DN 50 × DN 50` → `DN 50` нежелательно когда 2 query-параметра

**Суть:** В `FamilySizeFormatter.BuildDisplayName` добавлена проверка `allSame` — если все DN одинаковые, показывается `"DN 50"`. Но пользователь хочет видеть `"DN 50 × DN 50"` если у семейства 2 РАЗНЫХ query-параметра (напр. `DN_Проход` и `DN_Ответвление`), даже если их текущие значения совпадают.

**Пример:** Тройник `DN 65 × DN 65` (проход=65, ответвление=65). В таблице поиска есть строки `65×65`, `65×50`, `50×50` и т.д. Пользователь хочет видеть: `"DN 65 × DN 65"`, а не `"DN 65"`.

**Но:** Кран шаровый с одним параметром `"DN"` на оба коннектора — ДОЛЖЕН показывать `"DN 50"` (один параметр).

**Решение:** `BuildDisplayName` и `BuildAutoSelectDisplayName` нужно знать не количество коннекторов, а количество **уникальных query-параметров**. Это информация есть только в `RevitLookupTableService` — именно там известно сколько столбцов CSV управляют размерами.

**Что сделать:**
1. Добавить в `SizeTableRow` поле `int UniqueQueryParameterCount` — количество уникальных query-параметров из формулы `size_lookup(...)`
2. Копировать это поле в `FamilySizeOption.UniqueParameterCount`
3. Изменить `FamilySizeFormatter.BuildDisplayName` — принимать `int uniqueParameterCount`:
   - Если `uniqueParameterCount <= 1` → `"DN {X}"` (один параметр, даже если 10 коннекторов)
   - Если `uniqueParameterCount >= 2` → всегда `"DN {target} × DN {other1} × ..."` (без упрощения)
4. Обновить все вызовы `BuildDisplayName` / `BuildAutoSelectDisplayName`

**Файлы для изменения:**
- `SmartCon.Core/Models/SizeTableRow.cs` — добавить `UniqueQueryParameterCount`
- `SmartCon.Core/Models/FamilySizeOption.cs` — добавить `UniqueParameterCount`
- `SmartCon.Core/Models/FamilySizeFormatter.cs` — `BuildDisplayName(connectorRadiiFt, targetConnIdx, uniqueParamCount)`
- `SmartCon.Revit/Parameters/RevitLookupTableService.cs` — `ExtractRowsFromTable`: записать `queryParams.Count` в каждую `SizeTableRow`
- `SmartCon.Revit/Parameters/RevitDynamicSizeResolver.cs` — передавать `uniqueParamCount` в форматтер
- `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` — передавать `uniqueParamCount` в форматтер

---

### Баг 2: Кран R910 показывает 2 DN вместо 1 (проблема `connectorParamMap`)

**Суть:** Кран шаровый R910 имеет один параметр `"DN"` который управляет обоими коннекторами. `connectorParamMap` = `{1: "DN", 2: "DN"}`. После фикса `ConnectorIndices: List<int>`, столбец "DN" корректно применяется к обоим коннекторам.

Но в логах видно `DN 40 × DN 15` — значит второй коннектор НЕ получает значение из CSV. Причина: `FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam` может не найти параметр для второго коннектора из-за того что origin коннектора в family space не совпадает с ConnectorElement в family doc. Если `connectorParamMap` содержит только `{1: "DN"}` (без conn2), то conn2 получает `currentRadii` (DN 15 — текущий радиус трубы).

**Диагностика:** Нужно добавить логирование в `GetAllSizeRows` — выводить полный `connectorParamMap` после построения. Если conn2 отсутствует — проблема в `FamilyParameterAnalyzer`.

**Возможные причины:**
1. `FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam` для второго коннектора возвращает `null` (не находит ConnectorElement по координате)
2. Второй коннектор привязан к другому параметру (не "DN")
3. Второй коннектор вообще не имеет привязки к Radius/Diameter

**Решение:**
1. Добавить расширенное логирование `connectorParamMap` в `GetAllSizeRows`:
   ```
   SmartConLogger.Lookup($"  connectorParamMap: [{string.Join(", ", connectorParamMap.Select(kvp => $"conn[{kvp.Key}]='{kvp.Value}'"))}]");
   ```
2. Если conn2 отсутствует в `connectorParamMap` — это баг `FamilyParameterAnalyzer`, не текущего кода
3. Если conn2 присутствует с тем же `"DN"` — после фикса `ConnectorIndices: List<int>` должно работать корректно
4. Если conn2 присутствует с другим параметром — значит семейство имеет 2 разных DN-параметра

**Важное замечание:** Даже если `connectorParamMap` корректен, `ExtractRowsFromTable` может работать неверно. Нужно проверить что `queryParams` из `FindQueryParamsForTable` возвращает список, где `"DN"` встречается один раз (а не дважды). Если CSV имеет один столбец `"DN"`, он должен мапиться на все коннекторы с параметром `"DN"`.

**Файлы для изменения:**
- `SmartCon.Revit/Parameters/RevitLookupTableService.cs:190-206` — логирование `connectorParamMap`
- `SmartCon.Revit/Parameters/RevitLookupTableService.cs:271-288` — логирование `matchingConnectors` для каждого query param

---

### Баг 3: Тройник ADSK — только 2 query-параметра но видим 3 DN

**Суть:** Тройник `ADSK_СтальСварка_Тройник_ГОСТ17376-2001` имеет:
- 3 коннектора: conn0 (проход1), conn1 (проход2), conn2 (ответвление)
- 2 query-параметра: `Условный радиус` (проход) и `Условный радиус_2` (ответвление) — проход1 и проход2 разделяют один параметр
- CSV-таблица имеет строки вида: `DN_Проход=65, DN_Ответвление=50`

**Текущая проблема:**
1. `connectorParamMap` может содержать 3 записи: `{0: "Условный радиус", 1: "Условный радиус_1" или "Условный радиус", 2: "Условный радиус_2"}`
2. Если conn0 и conn1 оба → `"Условный радиус"`, CSV столбец `"Условный радиус"` мапится на оба → корректно
3. Но `queryParams` из `FindQueryParamsForTable` может вернуть `["Условный радиус", "Условный радиус_2"]` — 2 query-параметра
4. DisplayName должен показывать **2 DN**: `DN 65 × DN 50` (проход × ответвление), а не 3 DN

**Что происходит сейчас:**
- `BuildDisplayName` получает `connectorRadiiFt` с 3 коннекторами → показывает `"DN X × DN Y × DN Z"`
- Но пользователь хочет видеть только уникальные query-параметры: `"DN X × DN Y"` (2 параметра)
- Дополнительно, `currentRadii` заполняет 3-й коннектор текущим значением (DN 65), создавая фантомные строки

**Решение (то же что и Баг 1):**

Решение единое — `UniqueQueryParameterCount`:

1. `queryParams.Count` из `FindQueryParamsForTable` = количество уникальных столбцов CSV
2. Для тройника: `queryParams = ["Условный радиус", "Условный радиус_2"]` → `Count = 2`
3. DisplayName: `"DN 65 × DN 50"` (2 числа, без дублирования)
4. Формат: **целевой query-параметр первый**, остальные за ним

**НО:** `BuildDisplayName` сейчас работает с `connectorRadiiFt` (по коннекторам). Нужно переключить на работу с **query-параметрами**.

**Архитектурное решение:**

Добавить в `SizeTableRow` информацию о маппинге query-параметров:

```csharp
public sealed record SizeTableRow
{
    public required int TargetColumnIndex { get; init; }
    public required double TargetRadiusFt { get; init; }
    public required IReadOnlyDictionary<int, double> ConnectorRadiiFt { get; init; }

    // NEW: query-parameter values from CSV (key = 0-based index of query param)
    public required IReadOnlyList<double> QueryParameterRadiiFt { get; init; } = [];

    // NEW: count of unique query parameters
    public required int UniqueQueryParameterCount { get; init; } = 1;
}
```

В `ExtractRowsFromTable`:
```csharp
// После парсинга CSV строки:
var queryParamRadii = new List<double>();
foreach (var col in columns)
{
    var c = cols[col.CsvColIndex].Trim().Trim('"');
    if (TryParseRevitValue(c, out double val))
        queryParamRadii.Add(val / 2.0 / 304.8);
}

result.Add(new SizeTableRow
{
    TargetColumnIndex = targetColIndex,
    TargetRadiusFt = targetRadiusFt,
    ConnectorRadiiFt = connectorRadii,
    QueryParameterRadiiFt = queryParamRadii,
    UniqueQueryParameterCount = queryParams.Count  // СЮДА!
});
```

В `FamilySizeFormatter`:
```csharp
public static string BuildDisplayName(
    IReadOnlyList<double> queryParamRadiiFt,
    int targetColumnIndex)
{
    if (queryParamRadiiFt.Count == 0) return "DN ?";
    if (queryParamRadiiFt.Count == 1) return $"DN {ToDn(queryParamRadiiFt[0])}";

    // targetColumnIndex is 1-based, convert to 0-based
    int targetIdx = targetColumnIndex - 1;
    if (targetIdx < 0 || targetIdx >= queryParamRadiiFt.Count)
        targetIdx = 0;

    var targetDn = ToDn(queryParamRadiiFt[targetIdx]);
    var parts = new List<string> { $"DN {targetDn}" };
    for (int i = 0; i < queryParamRadiiFt.Count; i++)
    {
        if (i == targetIdx) continue;
        parts.Add($"DN {ToDn(queryParamRadiiFt[i])}");
    }
    return string.Join(" × ", parts);
}
```

**Аналогично для `BuildAutoSelectDisplayName`:**

Вместо `connectorRadiiFt` (по коннекторам) передавать `queryParamRadiiFt` (по query-параметрам). Для построения АВТОПОДБОР в `LoadDynamicSizes` нужно извлечь текущие значения query-параметров из текущих коннекторов (по `connectorParamMap`).

**В `FamilySizeOption`:**
```csharp
// NEW: радиусы по query-параметрам (для DisplayName)
public IReadOnlyList<double> QueryParameterRadiiFt { get; init; } = [];

// NEW: количество уникальных query-параметров
public int UniqueParameterCount { get; init; } = 1;

// NEW: индекс целевого query-параметра (1-based, из SizeTableRow.TargetColumnIndex)
public int TargetColumnIndex { get; init; } = 1;
```

---

## Сводная таблица изменений

### Модели (SmartCon.Core)

| Файл | Изменение |
|---|---|
| `SizeTableRow.cs` | Добавить `QueryParameterRadiiFt`, `UniqueQueryParameterCount` |
| `FamilySizeOption.cs` | Добавить `QueryParameterRadiiFt`, `UniqueParameterCount`, `TargetColumnIndex` |
| `FamilySizeFormatter.cs` | Переписать `BuildDisplayName` на работу с query-параметрами вместо коннекторов |

### Revit (SmartCon.Revit)

| Файл | Изменение |
|---|---|
| `RevitLookupTableService.cs` — `ExtractRowsFromTable` | Записывать `QueryParameterRadiiFt` и `UniqueQueryParameterCount = queryParams.Count` |
| `RevitLookupTableService.cs` — `GetAllSizeRows` | Расширенное логирование `connectorParamMap` |
| `RevitDynamicSizeResolver.cs` — `GetAvailableFamilySizes` | Копировать query-param данные в `FamilySizeOption`, вызывать обновлённый `BuildDisplayName` |

### ViewModel (SmartCon.PipeConnect)

| Файл | Изменение |
|---|---|
| `PipeConnectEditorViewModel.cs` — `LoadDynamicSizes` | Строить `BuildAutoSelectDisplayName` из query-параметров (нужен маппинг conn→param) |
| `PipeConnectEditorViewModel.cs` — `RefreshAutoSelectSize` | Аналогично |
| `PipeConnectEditorViewModel.cs` — `ChangeDynamicSize` | Без изменений (работает с `AllConnectorRadii` — корректно) |

### Тесты (SmartCon.Tests)

| Файл | Изменение |
|---|---|
| `FamilySizeFormatterTests.cs` | Обновить тесты: `BuildDisplayName` теперь принимает `queryParamRadiiFt` + `targetColumnIndex` |
| Добавить тесты | Сценарии: 1 параметр→1 DN, 2 параметра→2 DN (даже если значения одинаковые), 3 параметра→3 DN |

---

## Порядок реализации

### Шаг 1: Обновить модели
1. `SizeTableRow` — добавить поля
2. `FamilySizeOption` — добавить поля
3. `FamilySizeFormatter` — переписать методы

### Шаг 2: Обновить RevitLookupTableService
1. `ExtractRowsFromTable` — заполнить `QueryParameterRadiiFt` и `UniqueQueryParameterCount`
2. Добавить логирование

### Шаг 3: Обновить RevitDynamicSizeResolver
1. `GetAvailableFamilySizes` — копировать query-param данные
2. Вызывать обновлённый `BuildDisplayName`

### Шаг 4: Обновить ViewModel
1. `LoadDynamicSizes` — нужна информация о query-параметрах для АВТОПОДБОР
2. Вариант: передавать через `FamilySizeOption.UniqueParameterCount` + `QueryParameterRadiiFt`

### Шаг 5: Обновить тесты
1. Все тесты `FamilySizeFormatterTests` — новая сигнатура
2. Новые кейсы

### Шаг 6: Сборка + все тесты

---

## Ключевая инварианта

**Количество чисел DN в DisplayName = количество уникальных query-параметров в `size_lookup(...)`, НЕ количество коннекторов.**

- Кран 2 коннектора, 1 параметр "DN" → `"DN 50"`
- Отвод 2 коннектора, 1 параметр "ADSK_Диаметр условный" → `"DN 50"`
- Ниппель 2 коннектора, 2 параметра "BP_NominalDiameter_1", "BP_NominalDiameter_2" → `"DN 50 × DN 32"`
- Тройник 3 коннектора, 2 параметра "Условный радиус", "Условный радиус_2" → `"DN 65 × DN 50"`
- Крестовина 4 коннектора, 3 параметра → `"DN 65 × DN 50 × DN 50"`

---

## Пример маппинга для тройника ADSK

```
connectorParamMap:
  conn0 → "Условный радиус"     (проход1)
  conn1 → "Условный радиус"     (проход2, тот же параметр!)
  conn2 → "Условный радиус_2"   (ответвление)

queryParams = ["Условный радиус", "Условный радиус_2"]  // из size_lookup формулы

columns:
  col 0 (CSV col 1): "Условный радиус"    → ConnectorIndices = [0, 1]  // оба проходных!
  col 1 (CSV col 2): "Условный радиус_2"  → ConnectorIndices = [2]     // ответвление

CSV строка: "65, 50"
  → QueryParameterRadiiFt = [radius_DN65, radius_DN50]
  → UniqueQueryParameterCount = 2
  → DisplayName = "DN 65 × DN 50"   (целевой=col1="Условный радиус" → DN 65, другой=DN 50)
```

## Пример маппинга для крана R910

```
connectorParamMap:
  conn1 → "DN"
  conn2 → "DN"                     // ТЖЕ параметр!

queryParams = ["DN"]               // один query-параметр!

columns:
  col 0 (CSV col 1): "DN" → ConnectorIndices = [1, 2]

CSV строка: "50"
  → QueryParameterRadiiFt = [radius_DN50]
  → UniqueQueryParameterCount = 1
  → DisplayName = "DN 50"          // один параметр → один DN
```

## Пример маппинга для ниппеля Valtec

```
connectorParamMap:
  conn2 → "BP_NominalDiameter"       // прямой
  conn3 → "BP_NominalDiameter_2"     // переход

queryParams = ["BP_NominalDiameter", "BP_NominalDiameter_2"]  // ДВА параметра

columns:
  col 0: "BP_NominalDiameter"     → ConnectorIndices = [2]
  col 1: "BP_NominalDiameter_2"   → ConnectorIndices = [3]

CSV строка: "50, 32"
  → QueryParameterRadiiFt = [radius_DN50, radius_DN32]
  → UniqueQueryParameterCount = 2
  → DisplayName = "DN 50 × DN 32"  (целевой первый)
```
