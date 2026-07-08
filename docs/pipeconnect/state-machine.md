# PipeConnect: State Machine

> **Status:** Active | Загружать: при работе с логикой PipeConnect.

## Термины

| Термин | Значение |
|---|---|
| **Static** | Неподвижный элемент — **второй** клик. К нему присоединяют. |
| **Dynamic** | Движущийся элемент — **первый** клик. Его перемещают и подгоняют. |
| **CTC** | `ConnectionTypeCode` — тип соединения (1=Сварка, 2=Резьба и т.д.) |
| **Reducer** | Переходник сечения — фитинг с двумя коннекторами разных DN одного CTC |
| **Фитинг** | Переходник по типу соединения (сварка→резьба) или соединительная деталь |
| **DirectConnect** | Прямое соединение без промежуточного фитинга |
| **Chain** | Цепочка элементов, присоединённых к dynamic (кнопки +/− в UI) |

## Диаграмма состояний

```
[START]
  |
  v
[S1: AwaitingDynamicSelection]
  |  Пользователь кликает по первому элементу (движущемуся)
  |  Фильтр: только элементы с free-коннекторами (исключая ConnectorType.Curve)
  |  Определяем ближайший свободный коннектор к точке клика
  |-- ESC --> [Cancelled]
  v
[S1.1: ConnectorTypeCheck]
  |  Читаем Description ближайшего коннектора
  |-- Description заполнен --> парсим CTC --> записываем DynamicConnector --> S2
  |-- Description пустой --> открываем MiniTypeSelector рядом с курсором
  |   |-- Пользователь выбрал тип --> EditFamily: записываем код в Description --> S2
  |   |-- Пользователь отменил --> остаёмся в S1 (повторный выбор)
  v
[S2: AwaitingStaticSelection]
  |  Пользователь кликает по второму элементу (неподвижному)
  |  Тот же фильтр + MiniTypeSelector если Description пустой (аналогично S1.1)
  |-- ESC --> сброс DynamicConnector --> [Cancelled]
  v
[S3: AligningConnectors]
  |  1. Перемещение: Origin dynamic --> Origin static
  |  2. Поворот BasisZ: dynamic.BasisZ --> -static.BasisZ (антипараллельность)
  |  3. Снэп BasisX к ближайшему кратному 15 градусов
  |  4. Коррекция позиции после поворота
  |  5. RunInTransaction: применяем Transform к элементу (или цепочке если toggle включён)
  |-- Ошибка трансформации --> уведомление --> [Cancelled]
  v
[S4: ResolvingParameters]
  |  Сравниваем радиусы: static.Radius vs dynamic.Radius
  |-- Равны --> S5
  |-- Разные --> алгоритм подбора параметров (см. algorithms.md)
  |   |-- LookupTable: размер найден --> установить --> S5
  |   |-- Параметр экземпляра --> изменить --> S5
  |   |-- Параметр типа --> сменить TypeId --> S5
  |   |-- Формула --> FormulaSolver.SolveFor() --> S5
  |   |-- Ничего не помогло --> уведомление, S5 (с пометкой "нужен переходник")
  v
[S5: ResolvingFittings]
  |  Сравниваем CTC: static vs dynamic
  |  Загружаем правила из IFittingMapper
  |-- isDirectConnect + нет семейств --> ProposedFittings = [] --> S6
  |-- isDirectConnect + есть семейства --> фильтр по размерам --> автовыбор --> S6
  |-- !isDirectConnect --> обязателен переходник --> фильтр, автовыбор --> S6
  |-- Правило не найдено --> FindShortestFittingPath (Дейкстра в FittingMapper) --> S6
  |-- Путь не найден --> предупреждение, S6 (без фитинга)
  v
[S6: PostProcessing] -- Открывается PipeConnectEditor (модальное окно, см. ADR-043)
  |  Пользователь видит результат в модели + окно управления
  |
  |-- "Повернуть" --> Transaction("Rotate"): RotateElement вокруг Z --> обновить модель
  |-- "Изменить коннектор" --> выбрать другой free-коннектор
  |   |-- RollbackChainLevels: ChainDepth → 0 ДО выравнивания (предотвращает поломку сети)
  |   |-- CycleAndAlign: реалайн к новому коннектору (S3) — пользователь ВИДИТ ориентацию
  |   |-- S6.1: Проверить CTC нового коннектора (CtcGuessService / VirtualCtcStore)
  |   |-- S6.2: ReevaluateAfterCycle -- S5-re + S4-re для новой пары
  |-- "Примерить фитинг" --> Transaction("ChangeFitting"): удалить текущий, вставить новый
  |-- "Переместить всю сеть" (toggle) --> Transaction("MoveChain"): ConnectionGraph + Transform
  |
  |-- "Соединить" -->
  |   |-- Transaction("ConnectTo"): connector.ConnectTo() --> Commit
  |   |-- TransactionGroup.Assimilate() --> [Committed]
  |
  |-- "Отмена" / ESC / закрытие окна -->
  |   |-- TransactionGroup.RollBack() --> [Cancelled]
```

## Правила переходов

1. **S1 -> S2:** Только после успешной записи DynamicConnector (с валидным CTC)
2. **S2 -> S3:** Только после успешной записи StaticConnector
3. **S3 -> S4 -> S5:** Автоматические переходы, без участия пользователя
4. **S5 -> S6:** Автоматический переход, открытие модального окна PipeConnectEditor
5. **S6 -> Committed:** Только по нажатию "Соединить"
6. **Любое -> Cancelled:** ESC или ошибка

## Матрица решений: что происходит при несовпадении

| Ситуация | Autopodbor | Reducer | Фитинг | Соединить |
|---|---|---|---|---|
| DN совпадают, CTC совпадают | — | — | — | Прямое |
| DN не совпадают, CTC совпадают, автоподбор OK | dynamic → static DN | — | — | Прямое |
| DN не совпадают, CTC совпадают, автоподбор FAIL | ближайший DN | **вставить** | — | Через reducer |
| DN совпадают, CTC не совпадают | — | — | **вставить** | Через фитинг |
| DN не совпадают, CTC не совпадают, фитинг точный | dynamic → фитинг.conn2 | — | **вставить** | Через фитинг |
| DN не совпадают, CTC не совпадают, фитинг ближайший | — | **вставить** | **вставить** | Фитинг + reducer |
| Пользователь сменил размер (direct) | — | **вставить** | — | Через reducer |
| Пользователь сменил размер (фитинг) | фитинг переподбирается | если не точный — **вставить** | обновить | Фитинг ± reducer |

## Кейсы

### Кейс 1: DN совпадают, CTC совпадают
Прямое соединение. В списке фитингов — «Без фитинга (прямое соединение)». При «Соединить» — `ConnectTo(static, dynamic)`.

### Кейс 2: DN не совпадают, CTC совпадают
Система пытается автоподбор: `TrySetConnectorRadius(dynamic, staticRadius)`. Если успех — прямое соединение. Если нет — вставляется reducer из `FittingMappingRule.ReducerFamilies` (если настроен), иначе предупреждение.

### Кейс 3: CTC не совпадают
Обязателен фитинг-переходник. Правило ищется по `GetMappings(staticCTC, dynamicCTC)`. Фитинг вставляется, выравнивается, подбирается типоразмер. Если DN тоже не совпадают — добавляется reducer.

### Кейс 4: Пользователь меняет размер в UI
- Прямое соединение: `ChangeDynamicSize()` → проверка reducer → вставка/предупреждение.
- Через фитинг: переподбор фитинга + reducer при неточном совпадении.
- Выбор «АВТОПОДБОР»: откат `_userChangedSize`, размер к static, удаление reducer.

### Кейс 5: Chain+ (цепочка элементов)
Каждый уровень обрабатывается последовательно: Disconnect → Parent edge → AdjustSize → Align → ConnectTo. При несовпадении DN внутри цепочки — вставка reducer между parent и child.

### Кейс 6: Пользователь переключает фитинг в UI
Удаление текущего фитинга, вставка нового, выравнивание, `SizeFittingConnectors`, обновление UI. При выборе «Без фитинга» — удаление фитинга, возврат к прямому соединению.

### Кейс 7: Reducer в маппинге
Reducer-ы хранятся в `FittingMappingRule.ReducerFamilies` в ExtensibleStorage проекта (ADR-012). Условие: `FromType == ToType` (один CTC). Поиск: `FittingMapper.GetMappings(parentCTC, parentCTC)` → `ReducerFamilies[0]` по Priority.

### Кейс 8: Коннекторы без CTC
Если `Description` пустой — показывается `MiniTypeSelector`. После выбора `EditFamily` записывает CTC в `Description`. Если CTC фитинга неизвестен — используется `CtcGuessService` + `VirtualCtcStore` (ранее `FittingCtcSetupView`, теперь устаревший).

### Кейс 9: Отмена (RollBack)
Любое действие в PipeConnectEditor откатывается через `TransactionGroup.RollBack()`: удалённые фитинги восстанавливаются, размеры возвращаются, reducer-ы удаляются, элементы возвращаются на исходные позиции. Chain− откатывает по уровням через snapshot.

## Жизненный цикл TransactionGroup

```
TransactionGroup открывается в начале S3
  |
  +-- Transaction("Align")         --> S3
  +-- Transaction("SetParameter")  --> S4
  +-- Transaction("InsertFitting") --> S5
  +-- Transaction("Rotate")        --> S6 (по кнопке)
  +-- Transaction("ChangeFitting") --> S6 (по кнопке)
  +-- Transaction("ConnectTo")     --> S6 (по кнопке "Соединить")
  |
  +-- Assimilate() --> одна запись Undo
  или
  +-- RollBack()   --> полный откат
```
