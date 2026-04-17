# ADR-011: Отображение имени типоразмера в выпадающем списке DN

> **Статус:** Implemented
> **Дата:** 2026-04-17
> **Затрагиваемые модули:** PipeConnect — DN dropdown
> **Оценка:** ~150 строк нового кода, ~30 строк удалено, 6 файлов

---

## Контекст и проблема

В выпадающем списке DN (номинальный диаметр) модуля PipeConnect показывается только числовое значение: `"DN 50"`, `"DN 65 x DN 32"`. Но у семейства может быть **несколько типоразмеров (FamilySymbol)** с **одинаковым DN** и разными дополнительными характеристиками (угол, тип соединения, давление и т.д.).

**Пример:** Семейство "Отвод стальной" с FamilySymbol-ами "Отвод 15", "Отвод 30", "Отвод 45", "Отвод 90". Все имеют DN 50. В текущем выпадающем списке пользователь видит **одну** строку `"DN 50"` и не понимает, какой типоразмер выберет.

**Цель:** Если у одного DN есть несколько типоразмеров — показывать имя типоразмера в скобках: `"DN 50 (Отвод 90)"`, `"DN 50 (Отвод 45)"`. Если DN уникален — показывать без скобок: `"DN 50"`.

**Побочный эффект:** Удалить красное предупреждение `"Типоразмер: ... -> ..."` под ComboBox (информация теперь видна в самом списке).

---

## Решение

### Два пути загрузки данных

| Путь | Когда | Источник данных | SymbolName |
|---|---|---|---|
| **A. FamilySymbol** | Нет LookupTable | Перебор `family.GetFamilySymbolIds()` | **Уже есть** — `FamilySymbol.Name` |
| **B. LookupTable** | Есть `size_lookup` CSV | `FamilySizeTableManager.ExportSizeTable` | **Нужно вычислить** — маппинг строк на SymbolName |

Путь A — простое изменение дедупликации и форматирования.
Путь B — новая логика маппинга lookup-строк на FamilySymbol через non-size типовые параметры.

---

## Алгоритм для пути B (LookupTable)

### Шаг B1. Определить non-size query-параметры

Уже частично реализовано в `LookupColumnResolver.FindQueryParamsForTable()`:

1. Парсим формулу `size_lookup("Таблица", "Цель", 0, Param1, Param2, Param3, ...)` — получаем **все** query-параметры
2. Среди них определяем **size-параметры** — те, которые через цепочку формул управляют `CONNECTOR_RADIUS` (уже умеем через `FamilyParameterAnalyzer`)
3. **Оставшиеся** query-параметры = **non-size** параметры

### Шаг B2. Найти "листовые" non-size параметры

Для каждого non-size параметра — идём по цепочке зависимостей:

```
Параметр -> его формула -> переменные в формуле ->
  -> если у переменной есть формула -> рекурсивно
  -> если нет формулы -> это "листовой" параметр
```

Инструменты: `FormulaByName` (из `FamilyParameterSnapshot`), `FormulaSolver.ExtractVariables()`, `LookupColumnResolver.DependsOn()`.

### Шаг B3. Отфильтровать по IsInstance == false

Из листовых non-size параметров оставляем только **типовые** (`IsInstance == false`). Параметры экземпляра (IsInstance == true) игнорируются — они задаются пользователем для каждого экземпляра, не определяют типоразмер.

### Шаг B4. Маппинг lookup-строк на FamilySymbol

1. Читаем значение типового non-size параметра у **каждого** FamilySymbol (через `FamilyManager.CurrentType` или temp-транзакцию)
2. Читаем значение этого же параметра в **каждой** строке CSV
3. **Группируем** строки CSV по совпадению значений — каждая группа = один FamilySymbol
4. Присваиваем `SymbolName` из `FamilySymbol.Name`

### Шаг B5. Формирование DisplayName

```
Базовый DN: "DN 50" или "DN 50 x DN 32"  (уже есть)
           |
Проверка: есть ли другой FamilySizeOption с тем же базовым DN?
           |
  ДА -> "DN 50 (Отвод 90)"    — с именем типоразмера
  НЕТ -> "DN 50"               — без скобок
```

---

## Бизнес-кейсы

### Кейс 1: Отвод стальной, LookupTable + разные углы

**Семейство:** "Отвод стальной"
**FamilySymbols:** "Отвод 15", "Отвод 30", "Отвод 45", "Отвод 90"
**CSV:**

| DN | УГОЛ |
|---|---|
| 50 | 15 |
| 50 | 30 |
| 50 | 45 |
| 50 | 90 |
| 65 | 90 |

**size_lookup:** `size_lookup("Таблица", "Размер", 0, DN, УГОЛ)`

- `DN` -> SIZE (управляет CONNECTOR_RADIUS)
- `УГОЛ` -> NON-SIZE, leaf, type -> группирует на FamilySymbol

**Выпадающий список:**

```
DN 50 (Отвод 15)
DN 50 (Отвод 30)
DN 50 (Отвод 45)
DN 50 (Отвод 90)
DN 65 (Отвод 90)
```

> DN 65 единственный, но т.к. есть дубли DN 50 по другим типоразмерам, показываем имя везде.

---

### Кейс 2: Тройник, LookupTable + тип соединения

**Семейство:** "Тройник стальной"
**FamilySymbols:** "Тройник Сварка", "Тройник Резьба", "Тройник Фланец"
**CSV:**

| DN_1 | DN_2 | ТИП_СОЕД |
|---|---|---|
| 50 | 50 | 1 |
| 50 | 32 | 1 |
| 50 | 50 | 2 |
| 50 | 32 | 2 |
| 50 | 50 | 3 |

**size_lookup:** `size_lookup("Таблица", "Размер", 0, DN_1, DN_2, ТИП_СОЕД)`

- `DN_1`, `DN_2` -> SIZE
- `ТИП_СОЕД` -> NON-SIZE, type -> группирует

**Выпадающий список:**

```
DN 50 x DN 50 (Тройник Сварка)
DN 50 x DN 32 (Тройник Сварка)
DN 50 x DN 50 (Тройник Резьба)
DN 50 x DN 32 (Тройник Резьба)
DN 50 x DN 50 (Тройник Фланец)
```

---

### Кейс 3: Муфта, только FamilySymbol, без LookupTable

**Семейство:** "Муфта резьбовая"
**FamilySymbols:** "Муфта 15", "Муфта 20", "Муфта 25", "Муфта 32"
**LookupTable:** нет

Каждый FamilySymbol имеет **уникальный** DN — дублей нет.

**Выпадающий список:**

```
DN 15
DN 20
DN 25
DN 32
```

---

### Кейс 4: Труба (MEPCurve)

**Элемент:** Труба (Pipe)
FamilySymbol отсутствует. DN из `RoutingPreferenceManager -> Segment.GetSizes()`.

**Выпадающий список** (без изменений):

```
DN 15
DN 20
DN 25
DN 32
...
```

---

### Кейс 5: Затвор дисковый, LookupTable + давление

**Семейство:** "Затвор дисковый"
**FamilySymbols:** "Затвор PN10", "Затвор PN16", "Затвор PN25"
**CSV:**

| DN | ДАВЛЕНИЕ |
|---|---|
| 50 | 10 |
| 50 | 16 |
| 50 | 25 |
| 100 | 10 |
| 100 | 16 |

**size_lookup:** `size_lookup("Таблица", "DN", 0, DN_ВХ, ДАВЛЕНИЕ_ТИП)`

- `DN_ВХ` -> SIZE
- `ДАВЛЕНИЕ_ТИП` -> NON-SIZE, type -> группирует

**Выпадающий список:**

```
DN 50 (Затвор PN10)
DN 50 (Затвор PN16)
DN 50 (Затвор PN25)
DN 100 (Затвор PN10)
DN 100 (Затвор PN16)
```

---

### Кейс 6: Крестовина, instance non-size — НЕ группирует

**Семейство:** "Крестовина"
**FamilySymbols:** "Крестовина Стандарт" (1 символ)
**CSV:**

| DN_1 | DN_2 | МАТЕРИАЛ |
|---|---|---|
| 50 | 50 | "Сталь" |
| 50 | 50 | "Чугун" |
| 50 | 32 | "Сталь" |

**size_lookup:** `size_lookup("Таблица", "Размер", 0, DN_1, DN_2, МАТЕРИАЛ)`

- `DN_1`, `DN_2` -> SIZE
- `МАТЕРИАЛ` -> NON-SIZE, но **IsInstance = true** -> **игнорируем**

Семейство имеет 1 типоразмер. Дубли DN 50 x DN 50 склеиваются (одинаковы по радиусам).

**Выпадающий список:**

```
DN 50 x DN 50
DN 50 x DN 32
```

---

### Кейс 7: Переходник, только FamilySymbol

**Семейство:** "Переходник стальной"
**FamilySymbols:** "Переход 50x40", "Переход 50x32", "Переход 65x50"
**LookupTable:** нет

Каждый символ имеет 2 коннектора с разными DN. Комбинации уникальны.

**Выпадающий список:**

```
DN 50 x DN 40
DN 50 x DN 32
DN 65 x DN 50
```

---

### Кейс 8: Отвод, FamilySymbol с дублями DN

**Семейство:** "Отвод простой"
**FamilySymbols:** "Отвод 45", "Отвод 90" (без LookupTable, размеры "зашиты" в типы)
**LookupTable:** нет

Оба символа имеют DN 50 (одинаковый радиус коннектора).

**Выпадающий список:**

```
DN 50 (Отвод 45)
DN 50 (Отвод 90)
```

---

### Кейс 9: Клапан, несколько non-size type-параметров

**Семейство:** "Клапан обратный"
**FamilySymbols:** "Клапан PN16 Сталь", "Клапан PN16 Чугун", "Клапан PN25 Сталь"
**CSV:**

| DN | ДАВЛЕНИЕ | МАТЕРИАЛ_КОРПУСА |
|---|---|---|
| 50 | 16 | 1 |
| 50 | 25 | 1 |
| 50 | 16 | 2 |
| 80 | 16 | 1 |

**size_lookup:** `size_lookup("Таблица", "Размер", 0, DN, ДАВЛЕНИЕ, МАТЕРИАЛ_КОРПУСА)`

- `DN` -> SIZE
- `ДАВЛЕНИЕ` -> NON-SIZE, type
- `МАТЕРИАЛ_КОРПУСА` -> NON-SIZE, type
- **Комбинация** (ДАВЛЕНИЕ + МАТЕРИАЛ_КОРПУСА) -> определяет FamilySymbol

**Выпадающий список:**

```
DN 50 (Клапан PN16 Сталь)
DN 50 (Клапан PN25 Сталь)
DN 50 (Клапан PN16 Чугун)
DN 80 (Клапан PN16 Сталь)
```

---

### Кейс 10: Задвижка, цепочка из 3 формул

**Семейство:** "Задвижка клиновая"
**FamilySymbols:** "Задвижка 30с41нж", "Задвижка 30ч6бр"
**Формулы:**

```
CONNECTOR_RADIUS <- РАДИУС_КОННЕКТОРА
РАДИУС_КОННЕКТОРА = ДИАМЕТР_ВНУТР / 2
ДИАМЕТР_ВНУТР = size_lookup("Таблица", "D", 0, ДУ, ТИП_ЗАДВИЖКИ)
ТИП_ЗАДВИЖКИ = КОД_ТИПА              <- цепочка!
КОД_ТИПА -- leaf, type, IsInstance=false
```

**CSV:**

| ДУ | ТИП_ЗАДВИЖКИ |
|---|---|
| 50 | 1 |
| 50 | 2 |
| 100 | 1 |

**Анализ:**

1. `size_lookup` query params: `ДУ`, `ТИП_ЗАДВИЖКИ`
2. `ДУ` -> SIZE (через цепочку -> CONNECTOR_RADIUS)
3. `ТИП_ЗАДВИЖКИ` -> NON-SIZE
4. Цепочка: `ТИП_ЗАДВИЖКИ` -> формула содержит `КОД_ТИПА` -> leaf, type
5. Читаем `КОД_ТИПА` у каждого FamilySymbol -> маппим строки

**Выпадающий список:**

```
DN 50 (Задвижка 30с41нж)
DN 50 (Задвижка 30ч6бр)
DN 100 (Задвижка 30с41нж)
```

---

## План реализации

### Фаза 1: Извлечение non-size столбцов из LookupTable

**Файл:** `src/SmartCon.Revit/Parameters/RevitLookupTableService.cs`

В методе `ExtractRowsFromTable()`:

**1.1.** Определить `nonSizeColumnIndices` — столбцы из `columns`, не входящие в `sizeColumnIndices` и не привязанные к коннекторам (уже определяются как "unassigned" в строке 378).

**1.2.** В цикле по CSV-строкам (~строка 448), после обработки size-столбцов, извлечь non-size значения:

```csharp
var nonSizeValues = new Dictionary<string, string>();
foreach (var colIdx in nonSizeColumnIndices)
{
    var col = columns[colIdx];
    if (col.CsvColIndex >= cols.Length) continue;
    var cellValue = cols[col.CsvColIndex].Trim().Trim('"');
    nonSizeValues[col.ParameterName] = cellValue;
}
```

**1.3.** Записать в `SizeTableRow.NonSizeParameterValues` (поле уже объявлено в модели, никогда не заполнялось).

**1.4.** Изменить `DeduplicateRows()` (строка 523) — включить `NonSizeParameterValues` в ключ дедупликации:

```csharp
var key = string.Join("|", row.ConnectorRadiiFt
    .OrderBy(kvp => kvp.Key)
    .Select(kvp => $"{kvp.Key}:{kvp.Value:F8}"))
    + "|" + string.Join("|", row.NonSizeParameterValues
        .OrderBy(kvp => kvp.Key)
        .Select(kvp => $"{kvp.Key}={kvp.Value}"));
```

---

### Фаза 2: Определение non-size типовых параметров и маппинг на FamilySymbol

**Файл:** `src/SmartCon.Revit/Parameters/RevitDynamicSizeResolver.cs`

**2.1. Новый метод:** `FindNonSizeTypeParameters(familyDoc, lookupRows, connectorParamMap)`

Алгоритм:

1. Из первой lookup-строки берём `QueryParamNames` — все query-параметры
2. Определяем **size-параметры** — те, что есть в `connectorParamMap.Values` (управляют коннекторами)
3. Оставшиеся = **non-size** query-параметры
4. Для каждого non-size параметра:
   a. Ищем в `FamilyManager.Parameters` по имени
   b. Если у него формула — идём по цепочке через `FormulaByName` и `ExtractVariables()` пока не дойдём до leaf (без формулы)
   c. Проверяем leaf-параметр: `IsInstance == false`?
   d. Если да — это **типовой non-size параметр** — добавляем в результат

Возвращает: `List<string>` — имена leaf-параметров, которые являются типовыми non-size.

**2.2. Новый метод:** `MapRowsToSymbols(doc, instance, lookupRows, nonSizeTypeParams)`

Алгоритм:

1. Получить все `FamilySymbol` через `family.GetFamilySymbolIds()`
2. Для каждого символа (в `RunAndRollback`):
   a. `ChangeTypeId(symbolId)` + `Regenerate()`
   b. Прочитать значения non-size type-параметров с элемента (через `LookupParameter` -> `AsDouble()` / `AsString()`)
   c. Запомнить: `(SymbolName, Dictionary<paramName, value>)`
3. Для каждой lookup-строки:
   a. Сравнить её non-size значения (из `NonSizeParameterValues`) с значениями каждого символа
   b. Найти символ с совпадением (с допуском eps для числовых значений)
   c. Вернуть маппинг: `Dictionary<int, string>` (rowIndex -> SymbolName)

**2.3.** В `GetAvailableFamilySizes()` (LookupTable-ветка):

- Вызвать `FindNonSizeTypeParameters()` и `MapRowsToSymbols()`
- Присвоить `SymbolName` каждому `FamilySizeOption` из маппинга
- Передать `NonSizeParameterValues` из `SizeTableRow` в `FamilySizeOption`

**2.4.** Изменить `DeduplicateFamilyOptions()` — включить `SymbolName` в ключ:

```csharp
var key = string.Join("|", opt.AllConnectorRadii
    .OrderBy(kvp => kvp.Key)
    .Select(kvp => $"{kvp.Key}:{kvp.Value:F8}"))
    + "|" + (opt.SymbolName ?? "")
    + "|" + string.Join("|", opt.NonSizeParameterValues
        .OrderBy(kvp => kvp.Key)
        .Select(kvp => $"{kvp.Key}={kvp.Value}"));
```

---

### Фаза 3: Умное форматирование DisplayName

**Файл:** `src/SmartCon.Core/Models/FamilySizeFormatter.cs`

**3.1. Новый метод:**

```csharp
public static string AppendSymbolNameIfNeeded(
    string baseDisplayName,
    string? symbolName,
    IReadOnlyList<FamilySizeOption> allOptions)
{
    if (string.IsNullOrEmpty(symbolName)) return baseDisplayName;

    bool hasDuplicate = allOptions.Any(o =>
        o.DisplayName == baseDisplayName && o.SymbolName != symbolName);

    if (!hasDuplicate) return baseDisplayName;
    return $"{baseDisplayName} ({symbolName})";
}
```

Логика: если у данного DN нет другого типоразмера с таким же DisplayName — скобки не добавляем. Если есть дубль — добавляем `(SymbolName)`.

**3.2.** Вызвать в `GetAvailableFamilySizes()` на финальном шаге, **после** того как все `FamilySizeOption` собраны (и для LookupTable-, и для FamilySymbol-ветки):

```csharp
var result = SortByTargetDn(deduped);
for (int i = 0; i < result.Count; i++)
{
    var opt = result[i];
    var newDisplayName = FamilySizeFormatter.AppendSymbolNameIfNeeded(
        opt.DisplayName, opt.SymbolName, result);
    if (newDisplayName != opt.DisplayName)
        result[i] = opt with { DisplayName = newDisplayName };
}
return result;
```

---

### Фаза 4: Переключение FamilySymbol при выборе (LookupTable)

**Файл:** `src/SmartCon.PipeConnect/Services/PipeConnectSizeHandler.cs`

**4.1.** В `ChangeSize()`, перед `ApplyQueryParamsIfExists()` — если `selectedOption.SymbolName` отличается от `CurrentSymbolName`, переключить типоразмер:

```csharp
if (selectedOption.SymbolName is not null
    && selectedOption.CurrentSymbolName is not null
    && selectedOption.SymbolName != selectedOption.CurrentSymbolName)
{
    var inst = d.GetElement(dynId) as FamilyInstance;
    var family = inst?.Symbol?.Family;
    if (family is not null)
    {
        foreach (var symId in family.GetFamilySymbolIds())
        {
            var sym = d.GetElement(symId) as FamilySymbol;
            if (sym?.Name == selectedOption.SymbolName)
            {
                inst.Symbol = sym;
                d.Regenerate();
                break;
            }
        }
    }
}
```

Затем `ApplyQueryParamsIfExists()` установит size query-параметры.

**Для FamilySymbol-пути** (без LookupTable): `TrySetConnectorRadius` уже обрабатывает `ChangeTypeId` внутри. Изменений не требуется.

---

### Фаза 5: Удаление красного предупреждения

**Файлы:**

- `src/SmartCon.PipeConnect/Views/PipeConnectEditorView.xaml` — удалить TextBlock `SizeChangeInfo` (строки 126-129)
- `src/SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs`:
  - Удалить `[ObservableProperty] private string? _sizeChangeInfo` (строка 83)
  - Удалить `[ObservableProperty] private bool _hasSizeChangeInfo` (строка 84)
  - В `OnSelectedDynamicSizeChanged` — удалить логику предупреждения (строки 542-551), оставить только `ChangeDynamicSizeCommand.NotifyCanExecuteChanged()`
- `src/SmartCon.PipeConnect/Services/PipeConnectSizeHandler.cs`:
  - Удалить `BuildSizeChangeInfo()` (строки 150-158)
  - Удалить поле `SizeChangeInfo` из `SizeChangeResult` (строка 20)
  - Удалить вызов в `ChangeSize()` (строка 96)

---

## Сводка изменений по файлам

| Файл | Фаза | Изменение |
|---|---|---|
| `RevitLookupTableService.cs` | 1 | Извлечение non-size значений + дедупликация |
| `RevitDynamicSizeResolver.cs` | 2 | Маппинг строк на символы, проброс SymbolName |
| `FamilySizeFormatter.cs` | 3 | Метод `AppendSymbolNameIfNeeded()` |
| `PipeConnectSizeHandler.cs` | 4+5 | Переключение символа + удаление предупреждения |
| `PipeConnectEditorView.xaml` | 5 | Удалить TextBlock SizeChangeInfo |
| `PipeConnectEditorViewModel.cs` | 5 | Удалить свойства предупреждения |

## Инварианты (проверка)

- **I-09:** Вся логика маппинга (чтение FamilySymbol, формулы) — в `SmartCon.Revit`. Core получает только `SymbolName` строку.
- **I-10:** MVVM — XAML использует `DisplayMemberPath="DisplayName"`, ViewModel не трогает UI.
- **I-05:** FamilySymbol не хранится — только имя (`SymbolName` строка).
