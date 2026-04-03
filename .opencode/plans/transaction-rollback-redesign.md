# План: Полная переработка транзакций и отмены в PipeConnect

## Аудит изменений с последнего коммита

### Что было изменено (7 файлов)

#### 1. `PipeConnectSessionContext.cs` — добавлены поля для Cancel
- **Добавлено:** `PreChangePoint`, `PreChangeRotation`, `PreChangeCurveStart`, `PreChangeCurveEnd`, `IsLocationCurve`
- **Удалено:** `ParamTargetRadius` (теперь передаётся через `ParameterResolutionPlan`)
- **Зачем:** Сохранить исходное состояние dynamic элемента для восстановления при Cancel

#### 2. `PipeConnectCommand.cs` — убраны S3+S4 APPLY
- **Удалено:** Применение S4 (подгонка размера) и S3 (выравнивание) — перенесено в ViewModel
- **Добавлено:** Сохранение `PreChangePoint/Rotation/CurveStart/CurveEnd` из `LocationPoint/LocationCurve`
- **Добавлено:** `TransactionLogger` — логирование транзакций
- **Зачем:** S3+S4 теперь выполняются в ViewModel (в OnWindowLoaded) чтобы Cancel мог их откатить

#### 3. `PipeConnectEditorViewModel.cs` — полная переработка
- **Добавлено:** `ParameterResolutionPlan` передаётся в конструктор
- **Добавлено:** `ApplyS4S3()` — применяется в OnWindowLoaded внутри TransactionGroup
- **Добавлено:** `RestoreElementToInitialState()` — восстановление позиции и поворота при Cancel
- **Изменено:** Каждый ExternalEvent оборачивает свои транзакции в `TransactionGroup.Assimilate()`
- **Изменено:** Cancel удаляет фитинг + восстанавливает позицию/поворот

#### 4. `RevitTransactionService.cs` — добавлено логирование
- **Добавлено:** Логирование всех операций RunInTransaction, BeginGroupSession

#### 5. `RevitTransactionGroupSession.cs` — добавлено логирование, исправлен IsActive
- **Изменено:** `IsActive` теперь `!_closed` вместо `!_disposed && _group.HasStarted()`
- **Добавлено:** Логирование всех операций

#### 6. `PipeConnectExternalEvent.cs` — добавлено логирование
- **Добавлено:** Логирование Raise и Execute

#### 7. `PipeConnectEditorView.xaml.cs` — добавлено логирование
- **Добавлено:** Логирование Closing

### Что было сделано зря (эксперименты)

1. **TransactionGroup между ExternalEvent** — НЕ работает. Revit автоматически откатывает незакрытые TransactionGroup при выходе из ExternalEvent.Execute(). Это подтверждено Arnošt Löbel (Autodesk) и нашим тестированием.

2. **RevitTransactionGroupSession** — создан, но **не используется** в текущей реализации. TransactionGroup создаётся напрямую в ViewModel через `new TransactionGroup(doc, name)`.

3. **ITransactionGroupSession интерфейс** — создан ранее, но не нужен для финального подхода.

4. **SmartConFailurePreprocessor в RevitTransactionGroupSession** — добавлен, но сессия не используется.

### Ключевые выводы из тестирования

| Подход | Результат |
|--------|-----------|
| TransactionGroup между ExternalEvent | ❌ Revit откатывает автоматически |
| TransactionGroup внутри одного ExternalEvent | ✅ Работает, Assimilate = одна undo-запись |
| Cancel через RollBack группы | ❌ Группа уже закрыта Revit |
| Cancel через ручное восстановление позиции | ⚠️ Работает частично (баги с поворотом) |
| `LocationPoint.Rotation` — read-only | ✅ Нужно использовать `ElementTransformUtils.RotateElement` |

---

## ТЗ: Идеальная реализация

### Архитектура

```
PipeConnectCommand.Execute()
  ├── S1-S2: Выбор элементов (без транзакций)
  ├── S1.1-S2.1: Назначение типов коннекторов (отдельные транзакции, НЕ откатываются)
  ├── S3: Вычисление выравнивания (математика, без API)
  ├── S4: Анализ параметров (EditFamily, LookupTable — без транзакций)
  ├── S5: Подбор фитингов (чтение)
  ├── Сохранить PreChangeState (позиция + поворот dynamic элемента)
  └── ShowDialog() ← МОДАЛЬНОЕ окно (не Show!)
       ├── Все изменения внутри одной TransactionGroup
       ├── Кнопка "ОТМЕНА" → group.RollBack()
       └── Кнопка "СОЕДИНИТЬ" → group.Assimilate()
```

### Требования

#### 1. Модальное окно вместо modeless
- Использовать `ShowDialog()` вместо `Show()` + ExternalEvent
- Весь код выполняется на главном потоке Revit
- TransactionGroup живёт весь диалог
- Пользователь не может вращать модель пока окно открыто (это приемлемо)

#### 2. Единая TransactionGroup
- Открывается ДО любых изменений модели
- Закрывается только при СОЕДИНИТЬ (Assimilate) или ОТМЕНА (RollBack)
- Все операции (S3+S4, вставка фитинга, повороты, смена коннектора) — транзакции внутри группы
- Undo = одна запись "PipeConnect" после Assimilate

#### 3. Кнопка ОТМЕНА
- Один вызов `group.RollBack()` — откатывает ВСЕ изменения
- Не нужно вручную восстанавливать позиции/повороты
- Не нужно отслеживать созданные элементы

#### 4. Кнопка СОЕДИНИТЬ
- `group.Assimilate()` — все транзакции сливаются в одну undo-запись
- Окно закрывается

#### 5. Закрытие крестиком
- Эквивалентно ОТМЕНА → `group.RollBack()`

#### 6. Что НЕ откатывается
- Назначение типов коннекторов (S1.1/S2.1) — это отдельные транзакции ДО группы
- Это приемлемо: назначение типа — подготовка, не изменение модели

### Файлы для изменения

1. **`PipeConnectCommand.cs`**
   - Убрать `view.Show()`, заменить на `view.ShowDialog()`
   - Вернуть S3+S4 APPLY внутрь диалога (не в Command)
   - Убрать сохранение PreChangeState (не нужно с RollBack)
   - Убрать TransactionLogger (оставить только SmartConLogger)

2. **`PipeConnectEditorViewModel.cs`**
   - Убрать ExternalEvent полностью
   - Убрать `_totalRotationDeg`, `RestoreElementToInitialState`
   - Убрать `ParameterResolutionPlan` из конструктора
   - Создать TransactionGroup в конструкторе или в методе Init
   - Все операции — `_groupSession.RunInTransaction()`
   - Cancel → `_groupSession.RollBack()`
   - Connect → `_groupSession.Assimilate()`

3. **`PipeConnectSessionContext.cs`**
   - Убрать `PreChangePoint`, `PreChangeRotation`, `PreChangeCurveStart/End`, `IsLocationCurve`
   - Вернуть `ParamTargetRadius` (nullable double)

4. **`RevitTransactionGroupSession.cs`**
   - Оставить как есть (уже рабочий)

5. **`RevitTransactionService.cs`**
   - Убрать TransactionLogger

6. **Удалить:**
   - `TransactionLogger.cs` (не нужен)
   - Логирование из ExternalEvent, View

### Поток данных

```
Execute():
  1. Выбрать dynamic элемент → dynamicProxy
  2. Назначить тип коннектора (если нужно) → отдельная транзакция
  3. Выбрать static элемент → staticProxy
  4. Назначить тип коннектора (если нужно) → отдельная транзакция
  5. ComputeAlignment() → alignResult
  6. BuildResolutionPlan() → plan (анализ, без изменений)
  7. FittingMapper.GetMappings() → proposed
  8. Создать ViewModel (передаёт ctx, plan, services)
  9. Создать View, передать ViewModel
  10. view.ShowDialog() ← блокирует до закрытия
  11. return Result.Succeeded

ViewModel.Init():
  1. _groupSession = txService.BeginGroupSession("PipeConnect")
  2. Применить S4+S3 внутри группы
  3. Если есть фитинг → вставить внутри группы
  4. Обновить UI

ViewModel.Connect():
  1. ConnectTo внутри группы
  2. groupSession.Assimilate()
  3. Закрыть окно

ViewModel.Cancel():
  1. groupSession.RollBack()
  2. Закрыть окно

View.Closing:
  1. Если сессия активна → viewModel.Cancel()
```

### Ограничения Revit API

1. **TransactionGroup НЕ может жить между ExternalEvent** — Revit откатывает автоматически
2. **EditFamily запрещён внутри TransactionGroup** — анализ ДО группы, применение ВНУТРИ
3. **LocationPoint.Rotation — read-only** — использовать `ElementTransformUtils.RotateElement`
4. **Модальное окно блокирует UI** — пользователь не может вращать модель (приемлемо для данного кейса)

### Что получим

| Функция | Было | Станет |
|---------|------|--------|
| Undo (Ctrl+Z) | Много отдельных записей | Одна запись "PipeConnect" |
| Cancel | Ручное восстановление (хрупкое) | `group.RollBack()` (одна строка) |
| Закрытие крестиком | Ручное восстановление | `group.RollBack()` (одна строка) |
| Сложность кода | Высокая (трекинг состояния) | Низкая (RollBack всё делает) |
