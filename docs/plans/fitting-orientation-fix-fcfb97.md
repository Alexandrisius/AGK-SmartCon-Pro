# Исправление ориентации фитинга: единый план

Единый план устранения корневой причины: `AlignFittingToStatic` не может определить ориентацию фитинга, когда CTC на коннекторах фитинга отсутствуют или не совпадают напрямую с CTC static-элемента.

---

## Контекст: два кейса одного бага

### Кейс 1: PEX-GM (PEX труба → тройник)
- Труба DN25 (CTC=5) ↔ тройник DN20 (CTC=2, static)
- Фитинг PEX-GM: conn[2] CTC=5 R=12.5mm, conn[1] CTC=1 (гайка) R=10.0mm
- Стратегия 1 (CTC==static): нет коннектора с CTC=2 → fail
- Стратегия 2 (исключение): **оба** коннектора имеют определённый CTC ≠ static(2) → неоднозначно → fail
- **Distance fallback → неверная ориентация**

### Кейс 2: Полипропилен_НР (полипропиленовая труба → кран R910)
- Труба DN32 (CTC=4) ↔ кран R910 DN32 (CTC=4, static)
- Фитинг `ADSK_Полипропилен_ПереходникНР`: **conn[1] CTC=0, conn[2] CTC=0** — описание коннекторов в RFA пустое
- Стратегия 1: CTC=0 ≠ static(4) → fail
- Стратегия 2: CTC=0 → `IsDefined=false` → нет определённых CTC → fail
- **Distance fallback → работает случайно, ломается при другом порядке кликов**

---

## Root cause: единая причина для обоих кейсов

`AlignFittingToStatic` определяет ориентацию **исключительно по CTC на коннекторах фитинга**. Метод не получает информацию о том, какой CTC ожидается на dynamic-стороне.

### Причина 1: CTC на фитинге ≠ static CTC (Кейс 1)
Промежуточный фитинг (PEX-GM) имеет CTC=1 (гайка) и CTC=5 (пластик). Static=CTC 2, dynamic=CTC 5. Ни один коннектор фитинга не совпадает с static CTC=2. Обе стратегии 1 и 2 не могут определить ориентацию однозначно.

### Причина 2: CTC на фитинге не задан (Кейс 2)
Многие RFA-семейства фитингов не имеют заполненного «Описания соединителя» (RBS_CONNECTOR_DESCRIPTION). После `InsertFitting` коннекторы читают Description из RFA → CTC=0 для всех.

---

## Доказательство из логов (две сессии 2026-04-07)

### Сессия 1 (успешная): тройник VTr.580-582 ↔ труба
```
[FitAlign] conn[1] CTC=4 R=10,0mm (static CTC=4)    ← В RFA задан CTC=4
[FitAlign] conn[2] CTC=2 R=7,5mm (static CTC=4)     ← В RFA задан CTC=2
[FitAlign] Стратегия 1 (CTC match): fc1=conn[1], fc2=conn[2]  ← СРАБОТАЛА!
```
Автор RFA `ПереходникВР` задал CTC=4 на одном коннекторе → Стратегия 1 срабатывает.

### Сессия 2 (баг): кран R910 ↔ труба
```
[FitAlign] conn[1] CTC=0 R=16,0mm (static CTC=4)    ← Description ПУСТОЙ
[FitAlign] conn[2] CTC=0 R=12,5mm (static CTC=4)    ← Description ПУСТОЙ
[FitAlign] Стратегия 3 (distance fallback): fc1=conn[1], fc2=conn[2]
```
Автор RFA `ПереходникНР` НЕ задал описание коннекторов → distance fallback.

---

## План исправления (4 шага)

### Шаг 1: Передать `dynamicTypeCode` в `AlignFittingToStatic` (решает Кейс 1)

**Файл:** `IFittingInsertService.cs` — _уже обновлён_: параметр `ConnectionTypeCode dynamicTypeCode = default`.

**Файл:** `RevitFittingInsertService.cs`

Добавить **Стратегию 2 (dynamicTypeCode match)** — перед текущей Стратегией 2 (исключение):

```csharp
// Стратегия 2 (new): conn с CTC == dynamicTypeCode → fc2 (к dynamic), другой → fc1 (к static)
if (fitConn1 is null && dynamicTypeCode.IsDefined)
{
    var dynMatch = connCtcMap.FirstOrDefault(x =>
        x.Ctc.IsDefined && x.Ctc.Value == dynamicTypeCode.Value);
    if (dynMatch.Conn is not null)
    {
        fitConn2 = dynMatch.Conn;
        fitConn1 = fittingConns.FirstOrDefault(c => c.Id != fitConn2.Id);
        SmartConLogger.Info($"[FitAlign] Стратегия 2 (dynamicTypeCode match): fc2=conn[{fitConn2.Id}], fc1=conn[{fitConn1?.Id}]");
    }
}
```

Перенумеровать: текущая Стратегия 2 (исключение) → Стратегия 3, distance fallback → Стратегия 4.

**Почему решает Кейс 1:** PEX-GM conn[2] CTC=5. Правило говорит dynamicTypeCode=5. Новая стратегия находит conn с CTC==5 → fc2. Оставшийся conn[1] → fc1.

**Почему НЕ решает Кейс 2:** Оба CTC=0, `x.Ctc.IsDefined == false` → стратегия не сработает.

### Шаг 2: Передавать `dynamicTypeCode` из всех callers

**Файл:** `PipeConnectEditorViewModel.cs`

Добавить поле:
```csharp
private FittingMappingRule? _activeFittingRule;
```

Сохранять правило при выборе фитинга:
```csharp
_activeFittingRule = SelectedFitting?.Rule;
```

Helper для определения dynamic-стороны из правила:
```csharp
private ConnectionTypeCode ResolveDynamicTypeFromRule(FittingMappingRule? rule)
{
    if (rule is null) return default;
    if (rule.FromType.Value == _ctx.StaticConnector.ConnectionTypeCode.Value)
        return rule.ToType;
    return rule.FromType;
}
```

Передавать во всех 3 вызовах `AlignFittingToStatic`:
```csharp
var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);
fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
    doc, insertedId, _ctx.StaticConnector, _transformSvc, _connSvc,
    dynamicTypeCode: dynCtc);
```

**Файл:** `NetworkMover.cs` — `default` (нет Rule).

### Шаг 3: Интерактивное назначение CTC на фитинге (решает Кейс 2)

**Принцип:** Не назначать CTC автоматически. Вместо этого — показать пользователю мини-диалог, где он вручную сопоставит каждый коннектор фитинга с типом из справочника. Диалог вызывается **до** основного окна плагина, на этапе `PipeConnectCommand`, когда фитинг ещё не вставлен.

#### 3.1. Когда показывать диалог

В `PipeConnectCommand.Execute()`, **после S5 (GetMappings)** и **до S7 (создание ViewModel)**:

```
S1: клик dynamic → EnsureTypeCode (если CTC неизвестен)
S2: клик static  → EnsureTypeCode (если CTC неизвестен)
S4: BuildResolutionPlan
S5: GetMappings → proposed (список правил с фитингами)
    ↓
    **S5.5: НОВЫЙ ЭТАП — Проверка CTC на фитингах**
    Для каждого правила из proposed:
      Загрузить FamilySymbol из проекта
      Если CTC на коннекторах семейства не задан → показать диалог
    ↓
S6: BuildGraph + прогрев кеша
S7: Создание ViewModel + Init
```

#### 3.2. Как проверить CTC на семействе без вставки экземпляра

Использовать существующий механизм `FittingFamilyRepository` — `doc.EditFamily(family)` для чтения `RBS_CONNECTOR_DESCRIPTION` на ConnectorElement-ах:

```csharp
private static bool IsFittingCtcDefined(Document doc, FamilySymbol symbol)
{
    var family = symbol.Family;
    Document? familyDoc = null;
    try
    {
        familyDoc = doc.EditFamily(family);
        var connElems = new FilteredElementCollector(familyDoc)
            .OfCategory(BuiltInCategory.OST_ConnectorElem)
            .WhereElementIsNotElementType()
            .Cast<ConnectorElement>()
            .ToList();

        // Фитинг с 2 коннекторами — оба должны иметь определённый CTC
        return connElems.Count >= 2
            && connElems.All(ce =>
            {
                var desc = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
                var ctc = ConnectionTypeCode.Parse(desc);
                return ctc.IsDefined;
            });
    }
    finally
    {
        familyDoc?.Close(false);
    }
}
```

**Вызывается ВНЕ транзакции** — `doc.EditFamily` разрешён.

#### 3.3. Диалог `FittingCtcSetupView`

Модальное окно, аналогичное `MiniTypeSelectorView`, но для назначения CTC **каждому** коннектору фитинга.

**Содержимое:**
```
┌──────────────────────────────────────────────────┐
│  Назначьте типы коннекторов фитингу              │
│  Семейство: ADSK_Полипропилен_ПереходникНР       │
│  Типоразмер: Ø32 x G1"                           │
│                                                   │
│  ┌─────────────────────────────────────────────┐ │
│  │ Коннектор 1  (параметр: DN1 = 20 мм)       │ │
│  │   Тип: [▼ Наружная резьба (4)        ]     │ │
│  ├─────────────────────────────────────────────┤ │
│  │ Коннектор 2  (параметр: DN2 = 25 мм)       │ │
│  │   Тип: [▼ Полипропилен (5)           ]     │ │
│  └─────────────────────────────────────────────┘ │
│                                                   │
│                     [Отмена]  [OK]                │
└──────────────────────────────────────────────────┘
```

**Данные для каждой строки:**
- Имя параметра, управляющего размером коннектора (DN1, DN2, BP_NominalDiameter и т.д.) — получается через `GetAssociateFamilyParameterId` из `MEPFamilyConnectorInfo`
- Текущий размер коннектора — из значения параметра
- Выпадающий список типов — из `IFittingMappingRepository.GetConnectorTypes()`
- **Предвыбор:** если правило маппинга говорит `FromType=4, ToType=5` — автоматически предвыбрать CTC=4 и CTC=5 для коннекторов (но пользователь может изменить)

#### 3.4. ViewModel: `FittingCtcSetupViewModel`

```csharp
public sealed class FittingCtcSetupItem
{
    public int ConnectorIndex { get; init; }        // 0, 1, ...
    public string ParameterName { get; init; }      // "DN1", "DN2"
    public double DiameterMm { get; init; }          // текущий диаметр
    public ConnectorTypeDefinition? SelectedType { get; set; }  // выбранный пользователем
}

public sealed partial class FittingCtcSetupViewModel : ObservableObject
{
    public string FamilyName { get; }
    public string SymbolName { get; }
    public ObservableCollection<FittingCtcSetupItem> Connectors { get; }
    public IReadOnlyList<ConnectorTypeDefinition> AvailableTypes { get; }

    [ObservableProperty]
    private bool _isValid;  // все коннекторы имеют выбранный тип

    // Предвыбор на основе правила:
    // Если rule.FromType совпадает с staticCTC → коннектор static-стороны = FromType
    public void PreSelectFromRule(FittingMappingRule rule, ConnectionTypeCode staticCTC) { ... }
}
```

#### 3.5. Определение параметра коннектора из FamilySymbol (без вставки)

Для предзаполнения диалога нужно знать: какой параметр управляет каким коннектором. Это делается через `EditFamily`:

```csharp
private static List<FittingCtcSetupItem> BuildConnectorItems(
    Document doc, FamilySymbol symbol)
{
    var family = symbol.Family;
    var familyDoc = doc.EditFamily(family);
    try
    {
        var connElems = new FilteredElementCollector(familyDoc)
            .OfCategory(BuiltInCategory.OST_ConnectorElem)
            .WhereElementIsNotElementType()
            .Cast<ConnectorElement>()
            .OrderBy(ce => ce.Origin.X)  // упорядочить по позиции
            .ToList();

        var items = new List<FittingCtcSetupItem>();
        for (int i = 0; i < connElems.Count; i++)
        {
            var ce = connElems[i];
            // Читаем текущий Description
            var desc = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
            var currentCtc = ConnectionTypeCode.Parse(desc);

            // Определяем параметр размера через AssociatedParameters
            string paramName = GetConnectorParamName(ce, familyDoc);

            // Читаем диаметр из значения коннектора
            double diamFt = ce.Radius * 2;

            items.Add(new FittingCtcSetupItem
            {
                ConnectorIndex = i,
                ParameterName = paramName,
                DiameterMm = diamFt * 304.8,
                CurrentCtc = currentCtc,
                SelectedType = currentCtc.IsDefined ? FindTypeByCode(currentCtc.Value) : null
            });
        }
        return items;
    }
    finally
    {
        familyDoc.Close(false);
    }
}
```

#### 3.6. Запись CTC после подтверждения пользователем

После нажатия OK в диалоге — записать выбранные CTC в RBS_CONNECTOR_DESCRIPTION коннекторов семейства:

```csharp
private static void ApplyFittingCtcSetup(
    Document doc, FamilySymbol symbol, List<FittingCtcSetupItem> items)
{
    // SetFittingConnectorTypeCode требует ВНЕ транзакции проекта
    for (int i = 0; i < items.Count; i++)
    {
        if (items[i].SelectedType is null) continue;
        // Нужен connectorIndex в контексте экземпляра, а не familyDoc.
        // Для этого создадим временный экземпляр, назначим CTC, удалим.
        // ИЛИ: запишем напрямую через EditFamily.
    }
}
```

**Проблема:** `SetFittingConnectorTypeCode` принимает `connectorIndex` — индекс коннектора в `ConnectorManager` **экземпляра**. У нас нет экземпляра (фитинг ещё не вставлен). Но метод внутри себя делает `EditFamily` и ищет коннектор по позиции. Поэтому можно вызвать его для **предварительно вставленного** временного экземпляра.

**Альтернатива (более чистая):** Написать новый метод, который записывает CTC напрямую через `EditFamily` **без экземпляра**, ориентируясь на ConnectorElement по позиции в локальных координатах семейства.

#### 3.7. Интеграция в PipeConnectCommand

Место вызова: **между S5 и S6** (строки 124–130 в PipeConnectCommand.cs):

```csharp
// S5: GetMappings
var proposed = fittingMapper.GetMappings(
    staticProxy.ConnectionTypeCode, dynamicProxy.ConnectionTypeCode);
...

// ── S5.5: Проверка и назначение CTC на фитингах ──
EnsureFittingCtcDefined(doc, proposed, staticProxy.ConnectionTypeCode,
    dynamicProxy.ConnectionTypeCode, mappingRepo, familyConnSvc, dialogSvc);

// S6: BuildGraph + прогрев кеша
...
```

Метод `EnsureFittingCtcDefined`:
```csharp
private static void EnsureFittingCtcDefined(
    Document doc,
    IReadOnlyList<FittingMappingRule> rules,
    ConnectionTypeCode staticCTC,
    ConnectionTypeCode dynamicCTC,
    IFittingMappingRepository mappingRepo,
    IFamilyConnectorService familyConnSvc,
    IDialogService dialogSvc)
{
    var types = mappingRepo.GetConnectorTypes();
    if (types.Count == 0) return;

    foreach (var rule in rules)
    {
        if (rule.IsDirectConnect) continue;
        foreach (var fitting in rule.FittingFamilies)
        {
            var symbol = FindFamilySymbol(doc, fitting.FamilyName, fitting.SymbolName);
            if (symbol is null) continue;

            if (!IsFittingCtcDefined(doc, symbol))
            {
                // Показать диалог назначения CTC
                var items = BuildConnectorItems(doc, symbol, types);
                var setupVm = new FittingCtcSetupViewModel(
                    symbol.Family.Name, symbol.Name, items, types);
                setupVm.PreSelectFromRule(rule, staticCTC);

                var setupView = new FittingCtcSetupView(setupVm);
                setupView.ShowDialog();

                if (!setupVm.IsValid) continue; // пользователь отменил или не всё заполнил

                // Записать CTC в семейство (ВНЕ транзакции)
                ApplyFittingCtc(doc, symbol, setupVm.Connectors.ToList());
            }
        }
    }
}
```

**Ключевое:** диалог показывается **один раз для каждого семейства**, у которого CTC не задан. После назначения CTC записывается в RFA — при следующем использовании этого семейства диалог **не появится** (т.к. Description уже заполнен).

#### 3.8. Предвыбор на основе правила маппинга

Правило содержит `FromType` и `ToType`. Один из них совпадает с `staticCTC`, другой — с `dynamicCTC`.

Логика предвыбора:
- Если `rule.FromType == staticCTC` → коннектору, который "ближе к static" (определяем по контексту), назначаем `FromType`, другому — `ToType`
- Но на этапе S5.5 мы ещё не знаем, какой коннектор фитинга ближе к static (фитинг не вставлен)
- Поэтому **предлагаем оба варианта и помечаем как "рекомендуемый"** первый по совпадению с staticCTC

Для 2-коннекторных фитингов это не критично — пользователь видит оба типа и сам выбирает, какой коннектор какой тип получает.

### Шаг 4: Валидация — запрет продолжения при CTC=0

Добавить проверку **после** диалога (или если диалог был отменён):

```csharp
// В PipeConnectCommand, после S5.5
if (!AllFittingsHaveCtc(proposed, doc))
{
    dialogSvc.ShowWarning("SmartCon",
        "Не удалось назначить типы коннекторов для фитинга. " +
        "Выберите другой фитинг или назначьте типы вручную.");
    return Result.Cancelled;
}
```

---

## Итоговая схема потока

```
S1: Клик dynamic → EnsureTypeCode → CTC назначен ✓
S2: Клик static  → EnsureTypeCode → CTC назначен ✓
S4: BuildResolutionPlan
S5: GetMappings → список правил с фитингами
S5.5: **НОВЫЙ ЭТАП**
  ├─ Для каждого фитинга из правил:
  │   ├─ Проверить CTC на коннекторах (EditFamily → read Description)
  │   ├─ Если CTC=0 → показать диалог FittingCtcSetupView
  │   │   ├─ Пользователь назначает типы каждому коннектору
  │   │   └─ Записать CTC в RFA (EditFamily → SetDescription → LoadFamily)
  │   └─ После диалога: CTC задан ✓
  └─ Если пользователь отменил → return Cancelled
S6: BuildGraph + прогрев кеша
S7: ViewModel.Init()
  ├─ InsertFitting (NewFamilyInstance)
  ├─ AlignFittingToStatic
  │   ├─ Стратегия 1: CTC == staticCTC → fc1                    [Кейс 2 после назначения]
  │   ├─ Стратегия 2: CTC == dynamicTypeCode → fc2              [Кейс 1]
  │   ├─ Стратегия 3: один defined CTC ≠ static → fc2 (old 2)
  │   └─ Стратегия 4: distance fallback (last resort)
  ├─ SizeFittingConnectors
  └─ RealignAfterSizing
```

---

## Файлы к изменению

| Файл | Изменение |
|------|-----------|
| **`RevitFittingInsertService.cs`** | +параметр `dynamicTypeCode`, +Стратегия 2 (dynTypeCode match), перенумеровать стратегии |
| **`PipeConnectEditorViewModel.cs`** | Передавать `dynamicTypeCode` в 3 вызовах; `_activeFittingRule`; `ResolveDynamicTypeFromRule` |
| **`IFittingInsertService.cs`** | Уже обновлён |
| **`PipeConnectCommand.cs`** | +этап S5.5: проверка CTC на фитингах, вызов диалога, запись CTC |
| **`FittingCtcSetupView.xaml`** | **НОВЫЙ** — XAML диалога назначения CTC |
| **`FittingCtcSetupView.xaml.cs`** | **НОВЫЙ** — code-behind |
| **`FittingCtcSetupViewModel.cs`** | **НОВЫЙ** — ViewModel диалога |
| **`PipeConnectDialogService.cs`** | +метод `ShowFittingCtcSetup` |
| **`IDialogService.cs`** | +метод `ShowFittingCtcSetup` |

---

## Верификация

### Кейс 1 (PEX-GM): Шаги 1-2
```
[FitAlign] conn[2] CTC=5 R=12.5mm (static CTC=2)
[FitAlign] conn[1] CTC=1 R=10.0mm (static CTC=2)
[FitAlign] Стратегия 2 (dynamicTypeCode match): fc2=conn[2] (CTC=5==dynTypeCode), fc1=conn[1]
```
→ Ориентация верная. Диалог S5.5 не показывается (CTC уже заданы в RFA).

### Кейс 2 (Полипропилен_НР): Шаг 3
```
[S5.5] Фитинг 'ADSK_Полипропилен_ПереходникНР': CTC не задан → показан диалог
  Пользователь назначил: Коннектор 1 (DN1) → CTC=4, Коннектор 2 (DN2) → CTC=5
[S5.5] CTC записан в RFA
...
[FitAlign] conn[1] CTC=4 R=16.0mm (static CTC=4)
[FitAlign] conn[2] CTC=5 R=12.5mm (static CTC=4)
[FitAlign] Стратегия 1 (CTC match): fc1=conn[1], fc2=conn[2]
```
→ Ориентация верная.

---

## Что НЕ меняем

- `SizeFittingConnectors` — логика останется прежней
- `NetworkMover` — передаёт `default` для `dynamicTypeCode`
- `EnsureTypeCode` — назначение CTC на исходных элементах при клике — работает корректно
- Таблицу маппинга (`connector-mapping.json`) — структура не меняется
- Автоматическое назначение CTC — пользователь всегда назначает вручную через диалог

---

## Открытые вопросы

1. **Какой коннектор фитинга соответствует какой стороне (static/dynamic):** на этапе S5.5 фитинг ещё не вставлен. В диалоге мы показываем оба коннектора и предлагаем пользователю самому решить. Предвыбор на основе правила — рекомендация, не гарантия.

2. **Кеширование после LoadFamily:** после записи CTC через `EditFamily` + `LoadFamily` семейство перезагружается. Нужно убедиться, что последующий `FindFamilySymbol` возвращает актуальные данные.

3. **Множественные правила с одним семейством:** если одно семейство фигурирует в нескольких правилах — диалог показывается только один раз (CTC пишется в RFA навсегда).

4. **Фитинги с более чем 2 коннекторами:** текущая логика заточена под 2-коннекторные фитинги. Для тройников и крестовин (3-4 коннектора) потребуется адаптация — но сейчас такие фитинги не используются в роли адаптеров.
