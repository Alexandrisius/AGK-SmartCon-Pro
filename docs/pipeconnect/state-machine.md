# PipeConnect: State Machine

> Загружать: при работе с логикой PipeConnect.

## Диаграмма состояний

```
[START]
  |
  v
[S1: AwaitingStaticSelection]
  |  Пользователь кликает по элементу
  |  Фильтр: только элементы с free-коннекторами (исключая ConnectorType.Curve)
  |  Определяем ближайший свободный коннектор к точке клика
  |-- ESC --> [Cancelled]
  v
[S1.1: ConnectorTypeCheck]
  |  Читаем Description ближайшего коннектора
  |-- Description заполнен --> парсим ConnectionTypeCode --> записываем StaticConnector --> S2
  |-- Description пустой --> открываем MiniTypeSelector рядом с курсором
  |   |-- Пользователь выбрал тип --> EditFamily: записываем код в Description --> S2
  |   |-- Пользователь отменил --> остаёмся в S1 (повторный выбор)
  v
[S2: AwaitingDynamicSelection]
  |  Пользователь кликает по второму элементу
  |  Тот же фильтр + MiniTypeSelector если Description пустой (аналогично S1.1)
  |-- ESC --> сброс StaticConnector --> [Cancelled]
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
  |  Сравниваем ConnectionTypeCode: static vs dynamic
  |  Загружаем правила из IFittingMapper
  |-- isDirectConnect + нет семейств --> ProposedFittings = [] --> S6
  |-- isDirectConnect + есть семейства --> фильтр по размерам --> автовыбор --> S6
  |-- !isDirectConnect --> обязателен переходник --> фильтр, автовыбор --> S6
  |-- Правило не найдено --> FindShortestFittingPath (Дейкстра) --> S6
  |-- Путь не найден --> предупреждение, S6 (без фитинга)
  v
[S6: PostProcessing] -- Открывается PipeConnectEditor (немодальное окно)
  |  Пользователь видит результат в модели + окно управления
  |
  |-- "Повернуть" --> Transaction("Rotate"): RotateElement вокруг Z --> обновить модель
  |-- "Изменить коннектор" --> выбрать другой free-коннектор
  |   |-- Если Description пустой --> MiniTypeSelector поверх окна
  |   |-- Полный переалайн: вернуться к S3 с новой парой коннекторов
  |-- "Примерить фитинг" --> Transaction("ChangeFitting"):
  |   |-- Удалить текущий фитинг (если есть)
  |   |-- Вставить новый (реальный элемент в модели)
  |-- "Переместить всю сеть" (toggle) --> Transaction("MoveChain"):
  |   |-- Построить ConnectionGraph
  |   |-- Применить Transform ко всем Nodes
  |
  |-- "Соединить" -->
  |   |-- Transaction("ConnectTo"): connector.ConnectTo() --> Commit
  |   |-- TransactionGroup.Assimilate() --> [Committed]
  |
  |-- "Отмена" / ESC / закрытие окна -->
  |   |-- TransactionGroup.RollBack() --> [Cancelled]
```

## Правила переходов

1. **S1 -> S2:** Только после успешной записи StaticConnector (с валидным ConnectionTypeCode)
2. **S2 -> S3:** Только после успешной записи DynamicConnector
3. **S3 -> S4 -> S5:** Автоматические переходы, без участия пользователя
4. **S5 -> S6:** Автоматический переход, открытие окна PipeConnectEditor
5. **S6 -> Committed:** Только по нажатию "Соединить"
6. **Любое -> Cancelled:** ESC или ошибка

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
