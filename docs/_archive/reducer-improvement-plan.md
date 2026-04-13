# План: Улучшение работы Reducer (переходников сечения)

> Дата: 2026-04-08
> Статус: В разработке
> Контекст: Пользователь меняет размер dynamic-элемента через выпадающий список, но при нажатии «Соединить» размер затирается обратно.

---

## 1. Описание проблемы

### 1.1 Баг: ValidateAndFixBeforeConnect затирает пользовательский выбор размера

**Файл:** `src/SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs:1751-1767`

**Сценарий воспроизведения:**
1. Пользователь соединяет кран шаровой DN15 (dynamic) с трубой DN25 (static) — прямое соединение, без фитинга
2. S4 подгоняет размер крана под DN25 (автоподбор)
3. Открывается PipeConnectEditor — кран отображается DN25
4. Пользователь выбирает DN15 в выпадающем списке размеров → `ChangeDynamicSize()` успешно меняет на DN15
5. Пользователь нажимает «Соединить» → `Connect()` → `ValidateAndFixBeforeConnect()`
6. **ValidateAndFixBeforeConnect** видит `rErr > radiusEps` (DN15 ≠ DN25) и **насильно** подгоняет dynamic обратно под `_ctx.StaticConnector.Radius` (DN25)
7. Результат: кран снова DN25 — выбор пользователя потерян

**Код-виновник (строка 1754-1766):**
```csharp
// Прямое соединение: static ↔ dynamic
double rErr = Math.Abs(_ctx.StaticConnector.Radius - dynFresh.Radius);
if (rErr > radiusEps)
{
    // ВОТ ПРОБЛЕМА: всегда подгоняет dynamic под static, игнорируя выбор пользователя
    _paramResolver.TrySetConnectorRadius(
        doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, _ctx.StaticConnector.Radius);
}
```

### 1.2 Отсутствующий функционал: Reducer не вставляется для основного соединения

`NetworkMover.InsertReducer()` вызывается **только** из цепочки (`IncrementChainDepth`, строка 834).
Для основного соединения static ↔ dynamic (без фитинга) редьюсер **никогда не вставляется**.

**Последствие:** Если пользователь намеренно выбрал другой размер (DN15 вместо DN25), элемент просто соединяется с несовпадающими радиусами — Revit может не разрешить такое соединение.

### 1.3 Текущий UI не даёт обратной связи

Пользователь не видит в окне PipeConnectEditor:
- Что при несовпадении размеров будет вставлен reducer
- Какие переходники доступны
- Что произойдёт при нажатии «Соединить»

---

## 2. Целевое поведение

### 2.1 Основной кейс: Прямое соединение + смена размера

```
1. Пользователь соединяет кран DN15 с трубой DN25 (прямое соединение)
2. S4 подгоняет кран под DN25 → PipeConnectEditor открывается с краном DN25
3. Пользователь выбирает DN15 в выпадающем списке
4. → Кран меняется на DN15
5. → Система обнаруживает: DN15 ≠ DN25
6. → Автоматически вставляется reducer DN25/DN15 между трубой и краном
7. → Пользователь видит: кран DN15 + reducer + труба DN25
8. Пользователь нажимает «Соединить» → reducer корректно связывает оба элемента
```

### 2.2 Кейс с фитингом: Смена размера dynamic подбирает фитинг + reducer

```
1. Кран DN15 соединяется с трубой DN25 через муфту (фитинг)
2. Пользователь меняет размер крана на DN15
3. → Фитинг переподбирается под пару DN25/DN15 (текущее поведение — частично работает)
4. → Если точного типоразмера фитинга нет → ближайший к dynamic + reducer
5. → Reducer вставляется между фитингом и dynamic-элементом
```

### 2.3 Кейс цепочки: IncrementChainDepth (уже работает)

```
1. При присоединении элемента цепочки к родителю
2. TrySetConnectorRadius пробует подогнать размер child под parent
3. Если верификация не прошла → InsertReducer
4. Reducer вставляется автоматически
```

---

## 3. План реализации

### Этап 1: Флаг пользовательского выбора размера (Багфикс)

**Приоритет:** P0 (блокирующий)
**Файлы:** `PipeConnectEditorViewModel.cs`

**3.1.1 Добавить флаг:**
```csharp
private bool _userChangedSize;
```

**3.1.2 Установить в ChangeDynamicSize:**
```csharp
private void ChangeDynamicSize()
{
    if (SelectedDynamicSize is null || SelectedDynamicSize.IsAutoSelect) return;
    _userChangedSize = true;  // ← Добавить
    // ... остальной код
}
```

**3.1.3 Использовать в ValidateAndFixBeforeConnect:**
```csharp
// Прямое соединение: static ↔ dynamic
double rErr = Math.Abs(_ctx.StaticConnector.Radius - dynFresh.Radius);
if (rErr > radiusEps)
{
    if (_userChangedSize)
    {
        // Пользователь САМ выбрал другой размер — не подгонять!
        // Вместо этого — попытаться вставить reducer (Этап 2)
        SmartConLogger.Info("[Validate] Пользователь выбрал другой размер — пропуск подгонки");
    }
    else
    {
        // Автоподбор — подгоняем как раньше
        _paramResolver.TrySetConnectorRadius(
            doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex,
            _ctx.StaticConnector.Radius);
    }
}
```

**3.1.4 Сбросить флаг при смене фитинга:**
В `InsertFittingSilent` / при выборе «Без фитинга»:
```csharp
_userChangedSize = false;
```

**Критерии приёмки:**
- [ ] После `ChangeDynamicSize` размер не затирается при «Соединить»
- [ ] Без пользовательской смены размера автоподбор работает как раньше
- [ ] Предупреждение в лог при несовпадении радиусов

---

### Этап 2: Автоматическая вставка reducer при прямом соединении

**Приоритет:** P0 (блокирующий, зависит от Этапа 1)
**Файлы:** `PipeConnectEditorViewModel.cs`, `NetworkMover.cs` (возможно)

**3.2.1 Новый метод в ViewModel — `TryInsertReducerForDirectConnect`:**

Логика:
1. Определить CTC dynamic-элемента (из `_activeDynamic`)
2. Найти в маппинге `FittingMapper.GetMappings(ctc, ctc)` — правило с `ReducerFamilies`
3. Если найдено — вставить reducer через `NetworkMover.InsertReducer` или аналогичную логику
4. Reducer выравнивается к static-коннектору, dynamic выравнивается к conn2 reducer'а
5. `_currentFittingId = reducerId` — reducer становится текущим фитингом

**Вызов:**
- Из `ChangeDynamicSize()` — после смены размера, если `IsDirectConnect` и радиусы не совпадают
- Из `ValidateAndFixBeforeConnect()` — если `_userChangedSize && rErr > eps`

**3.2.2 UI обратная связь:**
- После вставки reducer обновить `StatusMessage`: «Вставлен переходник: [FamilyName] DN→DN»
- `AvailableFittings` обновить: убрать «Без фитинга», добавить reducer как текущий фитинг
- `_activeFittingConn2` обновить на conn2 reducer'а

**Критерии приёмки:**
- [ ] При смене размера в выпадающем списке reducer вставляется автоматически
- [ ] Reducer отображается в модели Revit немедленно
- [ ] Пользователь видит в StatusMessage информацию о вставленном reducer
- [ ] Если reducer не найден в маппинге — предупреждение, но без ошибки

---

### Этап 3: Смена размера при наличии фитинга + reducer

**Приоритет:** P1
**Файлы:** `PipeConnectEditorViewModel.cs`

**3.3.1 Обновить ChangeDynamicSize для кейса «фитинг + другой размер»:**

Текущее поведение:
- `ChangeDynamicSize` меняет размер dynamic
- Вызывает `InsertFittingSilentNoDynamicAdjust` → фитинг переподбирается

Целевое поведение:
- Если фитинг не имеет точного типоразмера для пары (staticRadius, newDynamicRadius):
  1. Подбирается ближайший к dynamic
  2. Если dynamic-коннектор фитинга ≠ dynamic элемента → вставляется reducer между фитингом и dynamic

**3.3.2 Логика в SizeFittingConnectors:**

После `TrySetFittingTypeForPair` возвращает `(staticExact, achievedDynRadius)`:
- Если `!staticExact` — редьюсер между static и фитингом (крайний случай)
- Если `achievedDynRadius != currentDynRadius` — редьюсер между фитингом и dynamic

Это частично реализовано через `adjustDynamicToFit`, но не покрывает случай когда подгонка dynamic невозможна.

**Критерии приёмки:**
- [ ] При смене размера dynamic с активным фитингом — фитинг переподбирается
- [ ] Если точного типоразмера нет — ближайший + reducer
- [ ] Reducer вставляется автоматически без ошибок

---

### Этап 4: UI — отображение reducer в списке фитингов

**Приоритет:** P2
**Файлы:** `PipeConnectEditorViewModel.cs`, `PipeConnectEditorView.xaml`

**3.4.1 Добавить reducer в AvailableFittings:**

Уже частично реализовано (строка 122-128):
```csharp
if (rule.FromType.Value == rule.ToType.Value && rule.ReducerFamilies.Count > 0)
{
    foreach (var reducer in rule.ReducerFamilies.OrderBy(f => f.Priority))
        AvailableFittings.Add(new FittingCardItem(rule, reducer, isReducer: true));
}
```

Но это работает только если `FromType == ToType` — один тип коннектора. Для прямого соединения с несовпадением размеров — reducer должен появляться в списке при `ExpectNeedsAdapter=true`.

**3.4.2 Визуальная индикация:**
- Reducer в списке отображается с префиксом 🔧 (уже есть в FittingCardItem)
- При выборе reducer — сразу вставляется в модель
- При выборе «Без фитинга» — reducer удаляется

**Критерии приёмки:**
- [ ] Reducer виден в выпадающем списке когда `ExpectNeedsAdapter=true`
- [ ] Выбор reducer вставляет его в модель
- [ ] Переключение между «Без фитинга» и reducer работает корректно

---

## 4. Зависимости

| Этап | Зависит от | Влияет на |
|---|---|---|
| 1 (флаг _userChangedSize) | — | Этап 2 |
| 2 (авто-reducer) | Этап 1 | Этап 3, 4 |
| 3 (фитинг + reducer) | Этап 2 | — |
| 4 (UI) | Этап 2 | — |

---

## 5. Риски

| Риск | Митигация |
|---|---|
| Reducer не найден в маппинге → fallback | Предупреждение пользователю, соединение без reducer |
| Вставка reducer ломает выравнивание | Повторный AlignFittingToStatic + коррекция позиции |
| Двойной reducer (цепочка + прямой) | Проверка: если reducer уже вставлен — не вставлять второй |
| TransactionGroup rollback не чистит reducer | `_currentFittingId = reducerId` → RollBack автоматически откатит |

---

## 6. Файлы для изменения

| Файл | Изменение |
|---|---|
| `src/SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs` | Основные правки: флаг, авто-reducer, ValidateAndFix |
| `src/SmartCon.Revit/Network/NetworkMover.cs` | Возможно: новая перегрузка InsertReducer для static↔dynamic |
| `src/SmartCon.PipeConnect/ViewModels/FittingCardItem.cs` | Без изменений (уже поддерживает IsReducer) |
| `src/SmartCon.Core/Models/FittingMappingRule.cs` | Без изменений (ReducerFamilies уже есть) |

---

## 7. Тестирование

### Ручные тест-кейсы

| # | Сценарий | Ожидаемый результат |
|---|---|---|
| 1 | Кран DN15 → Труба DN25, прямое, без смены размера | Кран подгоняется под DN25, соединяется |
| 2 | Кран DN15 → Труба DN25, сменить на DN15, Соединить | Кран остаётся DN15, вставляется reducer DN25/DN15 |
| 3 | Кран DN15 → Труба DN25, сменить на DN15, reducer не настроен | Кран DN15, предупреждение, соединение без reducer |
| 4 | Кран DN15 → Труба DN25, через фитинг, сменить размер | Фитинг переподбирается + reducer если нужно |
| 5 | Цепочка: элемент с другим DN | Reducer вставляется автоматически (уже работает) |
| 6 | Соединить → Отмена → повтор | RollBack корректно откатывает reducer |
