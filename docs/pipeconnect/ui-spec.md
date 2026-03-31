# PipeConnect: Спецификация UI

> Загружать: при работе с UI окнами PipeConnect.

---

## Общие правила UI

1. Все немодальные окна взаимодействуют с Revit **только** через `IExternalEventHandler` (инвариант I-01)
2. MVVM строго: .xaml.cs содержит только `DataContext = viewModel` (инвариант I-10)
3. Все кнопки биндятся к `ICommand` (RelayCommand из SmartCon.UI)
4. Открытие окон — через `IDialogService`, не `new Window().ShowDialog()`

### Паттерн взаимодействия WPF <-> Revit

```
WPF UI Thread                     Revit Main Thread
-----------------                 -----------------
ViewModel.ButtonClick()
  --> _externalEvent.Raise()  ------->  PipeConnectExternalEvent.Execute(app)
                                          --> _transactionService.RunInTransaction(...)
  <-- PropertyChanged  <--------------  Уведомление через dispatcher
ViewModel обновляет UI
```

---

## 1. PipeConnectEditor (финальное окно, S6)

**Файл View:** `SmartCon.PipeConnect/Views/PipeConnectEditorView.xaml`
**Файл VM:** `SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs`
**Тип:** Немодальное (modeless) окно

### Layout

```
+--------------------------------------------+
| SmartCon - PipeConnect                [X]  |
+--------------------------------------------+
|                                            |
| -- Коннектор --------------------------   |
| [v Connector 1 (Free) - Сварка]           |
| [Изменить]                                |
|                                            |
| -- Поворот ----------------------------   |
| [<-]  [ 0.00 ]  [->]      Шаг: [15 v]   |
|                                            |
| -- Фитинги ---------------------------   |
| o Без фитинга (прямое соединение)         |
| * СварнойШов DN50          [Примерить]    |
| o Переходник_С-Р DN50      [Примерить]    |
|                                            |
| [ ] Переместить всю сеть                  |
|                                            |
| [ Отмена ]               [ Соединить ]    |
+--------------------------------------------+
```

### Секция «Коннектор»

| Элемент | Тип | Binding | Описание |
|---|---|---|---|
| Выпадающий список | ComboBox | `SelectedConnector` | Все свободные коннекторы динамического элемента |
| Кнопка «Изменить» | Button -> ICommand | `ChangeConnectorCommand` | Смена коннектора -> переалайн. Если Description пустой -> MiniTypeSelector поверх окна |

### Секция «Поворот»

| Элемент | Тип | Binding | Описание |
|---|---|---|---|
| Кнопка ↺ | Button -> ICommand | `RotateLeftCommand` | Повернуть влево на шаг. Hotkey: Ctrl+Left |
| TextBox угла | TextBox | `RotationAngleDeg` | Угол в градусах. Two-way binding. Enter = применить |
| Кнопка ↻ | Button -> ICommand | `RotateRightCommand` | Повернуть вправо на шаг. Hotkey: Ctrl+Right |
| Шаг | ComboBox | `RotationStep` | Значения: 5, 10, 15, 30, 45, 90 градусов |

Поворот выполняется вокруг оси Z коннектора (BasisZ) через `RotateElement`.

### Секция «Фитинги»

| Элемент | Тип | Binding | Описание |
|---|---|---|---|
| Список фитингов | ListView + RadioButton | `ProposedFittings`, `SelectedFitting` | Карточки с именем семейства и DN |
| Вариант «Без фитинга» | RadioButton | `NoFittingSelected` | Прямое соединение (если isDirectConnect) |
| Кнопка «Примерить» | Button -> ICommand | `PreviewFittingCommand` | Реальная вставка фитинга через Transaction |

При «Примерить»: старый фитинг удаляется, новый вставляется (отдельная Transaction внутри TransactionGroup).

### Нижняя панель

| Элемент | Тип | Binding | Описание |
|---|---|---|---|
| «Отмена» | Button | `CancelCommand` | TransactionGroup.RollBack(). Hotkey: Escape |
| «Соединить» | Button | `CommitCommand` | ConnectTo + Assimilate(). Hotkey: Enter |
| Toggle «Переместить всю сеть» | CheckBox | `MoveEntireChain` | По умолчанию: false (одиночный элемент) |

### Поведение при закрытии окна
Закрытие крестиком [X] = «Отмена» (RollBack).

---

## 2. MiniTypeSelector (мини-окно выбора типа)

**Файл View:** `SmartCon.PipeConnect/Views/MiniTypeSelectorView.xaml`
**Файл VM:** `SmartCon.PipeConnect/ViewModels/MiniTypeSelectorViewModel.cs`
**Тип:** Модальное (modal) диалоговое окно

### Когда появляется
- При клике на коннектор с пустым Description (S1.1)
- При смене коннектора в финальном окне (S6), если новый коннектор без Description

### Позиционирование
Окно появляется **рядом с курсором мыши**, не по центру экрана.

### Layout

```
+---------------------------+
| Тип соединения            |
+---------------------------+
| > Сварка                  |
|   Резьба                  |
|   Раструб                 |
|   Фланец                  |
|   Пресс-фитинг            |
|   ...                     |
+---------------------------+
| [ Отмена ]     [ OK ]    |
+---------------------------+
```

### Данные
Список типов загружается из JSON (AppData) через `IFittingMappingRepository.GetConnectorTypes()`.
Новые типы, добавленные в окне маппинга, сразу доступны здесь.

### Результат
- OK: возвращает `ConnectorTypeDefinition`
- Отмена/ESC: возвращает `null`

### Запись в Description
После выбора типа:
1. `doc.EditFamily(family)` -> FamilyDocument
2. Найти ConnectorElement по индексу
3. Записать код в Description
4. `familyDoc.LoadFamily(doc, FamilyLoadOptions)` -> перезагрузить
5. Всё в одной Transaction

---

## 3. MappingEditor (окно управления маппингом)

**Файл View:** `SmartCon.PipeConnect/Views/MappingEditorView.xaml`
**Файл VM:** `SmartCon.PipeConnect/ViewModels/MappingEditorViewModel.cs`
**Тип:** Немодальное (modeless) окно

### Открытие
Кнопка «Настройки SmartCon» на Ribbon.

### Layout — Вкладка «Типы коннекторов»

```
+--------------------------------------------+
| Настройки SmartCon                    [X]  |
+--------------------------------------------+
| [Типы коннекторов] [Правила маппинга]      |
+--------------------------------------------+
|                                            |
| Код | Название      | Описание             |
| --- | ------------- | -------------------- |
|  1  | Сварка        | Сварное соединение   |
|  2  | Резьба        | Резьбовое            |
|  3  | Раструб       | Раструбное           |
|                                            |
| [+ Добавить]  [Удалить]  [Сохранить]      |
+--------------------------------------------+
```

### Layout — Вкладка «Правила маппинга»

```
+--------------------------------------------+
| [Типы коннекторов] [Правила маппинга]      |
+--------------------------------------------+
|                                            |
| От типа: [Сварка v]  К типу: [Резьба v]   |
| [ ] Совместимы без фитинга                 |
|                                            |
| Семейства фитингов:                        |
| Приоритет | Семейство         | Типоразмер |
| --------- | ----------------- | ---------- |
|     1     | [Переходник_С-Р v]| [* v]      |
|     2     | [Муфта_универ v]  | [DN50 v]   |
|                                            |
| [+ Добавить семейство]                     |
|                                            |
| [Сохранить]                                |
+--------------------------------------------+
```

### Колонка «Семейства» в правилах маппинга

Вместо ввода CSV — кнопка **«Выбрать...»** и поле `FamiliesSummary` (например: "Угольник DN50, Переходник DN50-40").
Кнопка открывает `FamilySelectorView` (модально). Результат сохраняется в `MappingRuleItem.FittingFamilies`.

### Источник семейств
Семейства загружаются при открытии окна через `IFittingFamilyRepository.GetEligibleFittingFamilies(doc)`.
Критерии: `OST_PipeFitting` + `PartType=MultiPort` + ровно 2 `ConnectorElement`.

### Хранение
JSON в `%APPDATA%/SmartCon/`:
- `connector-types.json`
- `fitting-mapping.json`

---

## 4. FamilySelectorView (окно выбора семейств)

**Файл View:** `SmartCon.PipeConnect/Views/FamilySelectorView.xaml`
**Файл VM:** `SmartCon.PipeConnect/ViewModels/FamilySelectorViewModel.cs`
**Тип:** Модальное (modal) диалоговое окно, 640×460

### Когда появляется
Кнопка **«Выбрать...»** в DataGrid правил маппинга (MappingEditorView, вкладка «Правила маппинга»).

### Layout

```
+----------------------------------------------------------+
| Выбор семейств фитингов                             [X]  |
+----------------------------------------------------------+
| Доступные семейства:      |         | Выбранные:          |
| (OST_PipeFitting,         | [▶▶     | (порядок = приоритет)|
|  MultiPort, 2 коннектора) |  Добав.] | +------------------+|
| +--------------------+   | [◀◀     | | 1. Угольник DN50  ||
| | Угольник DN50      |   |  Убрать] | | 2. Переходник     ||
| | Переходник DN50-40 |   |         | +------------------+||
| | Муфта DN50         |   |         | [▲ Вверх] [▼ Вниз]  |
| +--------------------+   |         |                      |
+----------------------------------------------------------+
| [ Отмена ]                              [ Подтвердить ]  |
+----------------------------------------------------------+
```

### ViewModel: `FamilySelectorViewModel`

| Свойство | Тип | Описание |
|---|---|---|
| `AvailableFamilies` | `ObservableCollection<string>` | Семейства, не выбранные, отсортированы A→Z |
| `SelectedFamilies` | `ObservableCollection<string>` | Выбранные, порядок = приоритет |
| `SelectedAvailable` | `string?` | Выделенный элемент в левом списке |
| `SelectedMapping` | `string?` | Выделенный элемент в правом списке |
| `Confirmed` | `bool` | true = подтверждено |

| Команда | Описание |
|---|---|
| `AddCommand` | Переместить `SelectedAvailable` → `SelectedFamilies`. CanExecute: `SelectedAvailable != null` |
| `RemoveCommand` | Вернуть `SelectedMapping` → `AvailableFamilies` (в алфавитной позиции). CanExecute: `SelectedMapping != null` |
| `MoveUpCommand` | Сдвинуть `SelectedMapping` вверх. CanExecute: индекс > 0 |
| `MoveDownCommand` | Сдвинуть `SelectedMapping` вниз. CanExecute: индекс < Count-1 |
| `ConfirmCommand` | Confirmed=true, RequestClose |
| `CancelCommand` | Confirmed=false, RequestClose |

### Результат
`GetResult()` → `IReadOnlyList<FittingMapping>?`
- null при отмене
- Список FittingMapping с Priority = позиция+1 при подтверждении
