# Шаблон нейминга файлов (FileNameTemplate)

> Загружать: при реализации `IFileNameParser` и UI таба «Нейминг».

---

## Обзор

Модуль ProjectManagement поддерживает универсальную систему разбора имени файла.
Координатор задаёт разделитель и описывает роль каждого блока.
Система находит блок-статус и заменяет его значение при шаринге.

---

## Модель данных

### FileNameTemplate

```csharp
public sealed record FileNameTemplate
{
    public string Delimiter { get; init; } = "-";
    public List<FileBlockDefinition> Blocks { get; init; } = [];
    public List<StatusMapping> StatusMappings { get; init; } = [];
}
```

### FileBlockDefinition

```csharp
public sealed record FileBlockDefinition
{
    public int Index { get; init; }
    public string Role { get; init; } = "";
    public string Label { get; init; } = "";
}
```

### StatusMapping

```csharp
public sealed record StatusMapping
{
    public string WipValue { get; init; } = "";
    public string SharedValue { get; init; } = "";
}
```

---

## Предустановленные роли

| Роль | Описание | Пример значения |
|---|---|---|
| `project` | Код проекта | `0001` |
| `originator` | Инициатор (компания) | `PPR` |
| `volume` | Том / зона | `001` |
| `level` | Уровень / этаж | `00001` |
| `type` | Тип документа | `01` |
| `discipline` | Дисциплина | `AR`, `ME`, `ST` |
| `number` | Номер документа | `M3` |
| `status` | Статус (WIP/Shared) | `S0`, `S1`, `S2` |
| `milestone` | Этап | `P01`, `P02` |
| `suitability` | Пригодность | `S1`, `S2`, `S3` |
| `revision` | Ревизия | `P01`, `P02` |
| `custom` | Пользовательский | — |

**Ограничение:** Только ОДИН блок может иметь роль `status`.
Именно этот блок трансформируется при шаринге.

---

## Алгоритм парсинга

### ParseBlocks

```
Вход: fileName = "0001-PPR-001-00001-01-AR-M3-S0.rvt"
      delimiter = "-"
      blocks = [ {Index:0, Role:"project"}, ..., {Index:7, Role:"status"} ]

1. Убрать расширение .rvt: "0001-PPR-001-00001-01-AR-M3-S0"
2. Split по delimiter: ["0001", "PPR", "001", "00001", "01", "AR", "M3", "S0"]
3. Для каждого FileBlockDefinition:
   - blocks[i].Index = i → значение = parts[i]
4. Вернуть Dictionary<string, string>:
   { "project"→"0001", "originator"→"PPR", ..., "status"→"S0" }
```

### TransformStatus

```
Вход: fileName, template, targetStatus = "S1"

1. ParseBlocks → получить все блоки
2. Найти блок с Role == "status" → текущее значение "S0"
3. Найти StatusMapping где WipValue == "S0" → SharedValue = "S1"
4. Собрать новое имя: заменить parts[statusIndex] на "S1"
5. Вернуть: "0001-PPR-001-00001-01-AR-M3-S1.rvt"
```

### Validate

```
1. Разделитель задан (не пустой)
2. Blocks не пустой
3. Есть хотя бы один блок с Role == "status"
4. StatusMappings не пустой
5. Нет дублирующихся WipValue в StatusMappings
6. Текущее имя файла split'ится на >= blocks.Count частей
7. Значение блока-статуса найдено в одном из WipValue
```

---

## Примеры конфигураций

### ISO 19650 (UK)

**Имя:** `0001-PPR-XX-00-00001-AR-DR-A-0001-S2-P01.rvt`
**Разделитель:** `-`
**Блоки:**

| # | Роль | Метка |
|---|---|---|
| 0 | project | Код проекта |
| 1 | originator | Инициатор |
| 2 | volume | Том |
| 3 | level | Уровень |
| 4 | type | Тип |
| 5 | discipline | Дисциплина |
| 6 | number | Номер |
| 7 | suitability | Пригодность |
| 8 | status | Статус |
| 9 | revision | Ревизия |

**Статусы:** `S1` → `S2`, `S2` → `S3`, `S3` → `S4`

### Упрощённый (Россия)

**Имя:** `0001-PPR-001-00001-01-AR-M3-S0.rvt`
**Разделитель:** `-`
**Блоки:**

| # | Роль | Метка |
|---|---|---|
| 0 | project | Код проекта |
| 1 | originator | Инициатор |
| 2 | volume | Том |
| 3 | level | Уровень |
| 4 | type | Тип |
| 5 | discipline | Дисциплина |
| 6 | number | Номер |
| 7 | status | Статус |

**Статусы:** `S0` → `S1`

### Подчёркивание как разделитель

**Имя:** `PROJ001_AR_PLAN_01_WIP.rvt`
**Разделитель:** `_`
**Блоки:**

| # | Роль | Метка |
|---|---|---|
| 0 | project | Код проекта |
| 1 | discipline | Дисциплина |
| 2 | type | Тип документа |
| 3 | number | Номер |
| 4 | status | Статус |

**Статусы:** `WIP` → `SHARED`, `SHARED` → `PUBLISHED`

---

## JSON-формат (для Import/Export)

```json
{
  "shareFolderPath": "\\\\server\\Project\\03_Shared\\",
  "syncBeforeShare": true,
  "purgeOptions": {
    "purgeRvtLinks": true,
    "purgeCadImports": true,
    "purgeImages": true,
    "purgePointClouds": true,
    "purgeGroups": true,
    "purgeAssemblies": true,
    "purgeSpaces": true,
    "purgeRebar": true,
    "purgeFabricReinforcement": true,
    "purgeUnused": true
  },
  "keepViewNames": [
    "TASK_3D_Общий",
    "CORD_План_1_этажа",
    "TASK_Разрез_А-А"
  ],
  "fileNameTemplate": {
    "delimiter": "-",
    "blocks": [
      { "index": 0, "role": "project", "label": "Код проекта" },
      { "index": 1, "role": "originator", "label": "Инициатор" },
      { "index": 2, "role": "volume", "label": "Том" },
      { "index": 3, "role": "level", "label": "Уровень" },
      { "index": 4, "role": "type", "label": "Тип документа" },
      { "index": 5, "role": "discipline", "label": "Дисциплина" },
      { "index": 6, "role": "number", "label": "Номер" },
      { "index": 7, "role": "status", "label": "Статус" }
    ],
    "statusMappings": [
      { "wipValue": "S0", "sharedValue": "S1" }
    ]
  }
}
```

---

## Тесткейсы для FileNameParser (unit-тесты)

### ParseBlocks
1. Корректное имя, 8 блоков, разделитель "-" → словарь 8 пар
2. Имя с расширением .rvt → расширение удалено
3. Блоков больше чем определено → лишние игнорируются
4. Блоков меньше чем определено → ошибка валидации

### TransformStatus
1. S0 → S1 (одна пара маппинга)
2. Несколько пар маппинга, текущий = WIP → Shared
3. Текущий статус не найден в маппинге → null
4. Нет блока со role="status" → null

### Validate
1. Валидная конфигурация → true
2. Пустой разделитель → false
3. Нет блоков → false
4. Нет блока status → false
5. Пустые StatusMappings → false
6. Дублирующиеся WipValue → false
7. Имя файла короче чем количество блоков → false
8. Значение статуса не найдено в WipValue → false с конкретным сообщением
