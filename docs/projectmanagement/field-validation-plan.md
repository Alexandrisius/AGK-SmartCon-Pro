# План: Валидация полей имени файла + рефакторинг Role → Field

> **Статус:** ✅ Реализовано (включено в v1.4.x)
> План ниже был успешно выполнен. Field Library и валидация реализованы.

> **Целевой аудитории:** AI-агент с нулевым контекстом.
> Читай AGENTS.md, docs/invariants.md, docs/architecture/dependency-rule.md перед началом.
> Этот документ — SSOT для задачи. Все детали здесь.

---

## Контекст

Модуль `SmartCon.ProjectManagement` реализует Share Project — перемещение Revit-модели из WIP в Shared
по стандарту ISO 19650. Сейчас имя файла разбирается на блоки по разделителю, и только блок «status»
трансформируется (S0→S1). Но **нет проверки допустимых значений** для остальных блоков — пользователь
может назвать файл как угодно и расшарить.

**Цель:** Добавить валидацию каждого поля (field) имени файла по списку допустимых значений,
визуальную обратную связь в UI и блокировку Share при невалидном имени.

**Заодно:** Переименовать `Role` → `Field` во всём коде, потому что в ISO 19650 «role» — это
участник проекта (Appointing Party, Lead Appointed Party), а не позиция в имени файла.
Правильный термин — **Field** (поле).

---

## Бизнес-кейсы

### Кейс 1: Строгий стандарт компании (ISO 19650)

Компания «ACME BIM» использует стандарт:

```
Имя файла: PRJ-ACME-ZZ-01-M-AR-001-S0.rvt
Разделитель: -
```

| Поле № | Имя поля     | Допустимые значения              | Значение в файле |
|--------|--------------|----------------------------------|------------------|
| 0      | project      | (любое)                          | PRJ              |
| 1      | originator   | ACME, BETA, GAMA                 | ACME             |
| 2      | volume       | ZZ, Z1, Z2, Z3                   | ZZ               |
| 3      | level        | (любое)                          | 01               |
| 4      | type         | M, S, D                          | M                |
| 5      | discipline   | AR, STR, MEP, CV                 | AR               |
| 6      | number       | (любое)                          | 001              |
| 7      | status       | S0, S1, S2, S3, S4               | S0               |

**Сценарий:** Пользователь открывает Settings → вкладка «Именование». Видит:
- Авто-разбитое имя файла на поля
- Поле `discipline` = `AR` — зелёная галочка (входит в [AR, STR, MEP, CV])
- Поле `originator` = `ACME` — зелёная галочка (входит в [ACME, BETA, GAMA])

Пользователь переименовывает файл в `PRJ-XXXX-ZZ-01-M-XX-001-S0.rvt` и нажимает Share:
- **Блокировка:** TaskDialog «Поле 'originator' = 'XXXX' недопустимо. Допустимые значения: ACME, BETA, GAMA»

### Кейс 2: Свободный стандарт (без валидации)

Фрилансер использует простой шаблон:

```
Имя файла: MY_PROJECT_PLAN_A.rvt
Разделитель: _
```

| Поле № | Имя поля | Допустимые значения | Значение в файле |
|--------|----------|---------------------|------------------|
| 0      | project  | (пусто = любое)     | MY               |
| 1      | name     | (пусто = любое)     | PROJECT          |
| 2      | type     | (пусто = любое)     | PLAN             |
| 3      | status   | A, B, C             | A                |

AllowedValues для полей 0-2 = пустой список → **любое значение допустимо**, валидация не блокирует.
Только поле `status` имеет ограничения.

### Кейс 3: Нестандартный разделитель

```
Имя файла: PROJ001.AR.PLAN.01.WIP.rvt
Разделитель: .
```

| Поле № | Имя поля | Допустимые значения |
|--------|----------|---------------------|
| 0      | project  | (любое)             |
| 1      | field    | AR, STR, MEP        |
| 2      | type     | PLAN, SEC, ELEV     |
| 3      | number   | (любое)             |
| 4      | status   | WIP, SHARED         |

### Кейс 4: Библиотека полей

BIM-менеджер настраивает стандарт ОДИН РАЗ:
1. Открывает Settings → вкладка «Именование» → кнопка «Библиотека полей...»
2. Создаёт определения полей:
   - Name: `discipline`, DisplayName: `Дисциплина`, AllowedValues: `AR, STR, MEP, CV`
   - Name: `originator`, DisplayName: `Инициатор`, AllowedValues: `ACME, BETA`
   - Name: `status`, DisplayName: `Статус`, AllowedValues: `S0, S1, S2, S3, S4`
3. На вкладке «Именование» при добавлении блока выбирает поле из библиотеки → AllowedValues подтягиваются автоматически
4. Экспортирует JSON-шаблон → отправляет всем проектировщикам
5. Проектировщики импортируют JSON → получают готовый стандарт с валидацией

---

## Терминология ISO 19650

| Термин        | Что означает                                      | Использование в коде  |
|---------------|---------------------------------------------------|-----------------------|
| **Field**     | Позиция в имени файла (project, discipline, etc.) | `FileBlockDefinition.Field` |
| **Field name**| Название поля (ключ)                              | `FieldDefinition.Name`     |
| **Field value**| Конкретное значение в данной позиции             | Строка из имени файла      |
| **AllowedValues** | Список допустимых значений для поля          | `List<string>`             |
| **Field Library** | Набор переиспользуемых определений полей     | `ShareProjectSettings.FieldLibrary` |

**НЕ ИСПОЛЬЗОВАТЬ термин «Role»** — в ISO 19650 это участники проекта.

---

## Изменения по файлам

### 1. Новая модель: `FieldDefinition` (SmartCon.Core)

**Файл:** `src/SmartCon.Core/Models/FieldDefinition.cs` (НОВЫЙ)

```csharp
namespace SmartCon.Core.Models;

public sealed record FieldDefinition
{
    public string Name { get; init; } = string.Empty;        // "discipline"
    public string DisplayName { get; init; } = string.Empty; // "Дисциплина"
    public string Description { get; init; } = string.Empty; // "Код инженерной дисциплины"
    public List<string> AllowedValues { get; init; } = [];   // ["AR", "STR", "MEP", "CV"]
}
```

### 2. Обновление `FileBlockDefinition` (SmartCon.Core)

**Файл:** `src/SmartCon.Core/Models/FileBlockDefinition.cs`

**Было:**
```csharp
public sealed record FileBlockDefinition
{
    public int Index { get; init; }
    public string Role { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    public static readonly string[] PredefinedRoles = [ ... ];
}
```

**Стало:**
```csharp
public sealed record FileBlockDefinition
{
    public int Index { get; init; }
    public string Field { get; init; } = string.Empty;       // было Role
    public string Label { get; init; } = string.Empty;
    public List<string> AllowedValues { get; init; } = [];   // НОВОЕ: пустой = любое значение
}
```

- `Role` → `Field`
- `PredefinedRoles` → **УДАЛИТЬ** (заменяется на FieldLibrary из настроек)
- `AllowedValues` — пустой список = нет ограничений, заполненный = только эти значения допустимы

### 3. Обновление `ShareProjectSettings` (SmartCon.Core)

**Файл:** `src/SmartCon.Core/Models/ShareProjectSettings.cs`

**Добавить:**
```csharp
public List<FieldDefinition> FieldLibrary { get; init; } = [];  // НОВОЕ
```

**Итоговая модель:**
```csharp
public sealed record ShareProjectSettings
{
    public string ShareFolderPath { get; init; } = string.Empty;
    public FileNameTemplate FileNameTemplate { get; init; } = new();
    public List<FieldDefinition> FieldLibrary { get; init; } = [];  // НОВОЕ
    public PurgeOptions PurgeOptions { get; init; } = new();
    public List<string> KeepViewNames { get; init; } = [];
    public bool SyncBeforeShare { get; init; } = true;
    public static ShareProjectSettings Empty => new();
}
```

### 4. Обновление `FileNameParser` (SmartCon.Core)

**Файл:** `src/SmartCon.Core/Services/Implementation/FileNameParser.cs`

#### 4a. Рефакторинг Role → Field

Все `block.Role` → `block.Field`. Все `role` переменные → `field`.

#### 4b. Новый метод: `ValidateDetailed`

```csharp
public sealed record BlockValidation(
    int Index,
    string Field,
    string Value,           // значение из файла
    bool IsValid,
    string? Error           // null если IsValid, иначе текст ошибки
);

public sealed record ValidationResult(
    bool IsValid,
    string Summary,
    List<BlockValidation> Blocks
);
```

**Алгоритм `ValidateDetailed`:**
1. Базовые проверки (как сейчас): разделитель, блоки, статус-поле, маппинги
2. Split имени файла по разделителю
3. Для каждого блока:
   - Получить значение: `parts[block.Index]`
   - Если `block.AllowedValues.Count > 0`:
     - Проверить `block.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase)`
     - Если нет → `BlockValidation(Index, Field, value, false, "Value 'XX' not allowed. Expected: AR, STR, MEP, CV")`
   - Если `block.AllowedValues.Count == 0`:
     → `BlockValidation(Index, Field, value, true, null)`
   - Особый случай: блок `status` — проверить что значение входит в `StatusMappings[].WipValue`
4. Вернуть `ValidationResult` с агрегацией

#### 4c. Обновить `TransformStatus`

Все `Role == "status"` → `Field == "status"`.

#### 4d. Обновить `ParseBlocks`

Словарь: `result[block.Field] = parts[block.Index]` (было `block.Role`).

#### 4e. Обновить интерфейс `IFileNameParser`

```csharp
public interface IFileNameParser
{
    string? TransformStatus(string fileName, FileNameTemplate template);
    (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template);
    ValidationResult ValidateDetailed(string fileName, FileNameTemplate template);  // НОВОЕ
    Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template);
}
```

### 5. Обновление `FileNameBlockItem` (ViewModel)

**Файл:** `src/SmartCon.ProjectManagement/ViewModels/FileNameBlockItem.cs`

**Было:**
```csharp
[ObservableProperty] private string _role = string.Empty;
```

**Стало:**
```csharp
[ObservableProperty] private string _field = string.Empty;          // было _role
[ObservableProperty] private string _allowedValuesText = string.Empty;  // НОВОЕ: comma-separated
[ObservableProperty] private string _currentFieldValue = string.Empty;  // НОВОЕ: значение из файла (readonly)
[ObservableProperty] private bool _isValid = true;                      // НОВОЕ: статус валидации
[ObservableProperty] private string? _validationError;                  // НОВОЕ: текст ошибки
```

**Конвертация AllowedValuesText ↔ List<string>:**
- getter: `string.Join(", ", AllowedValues)` (из FieldDefinition или шаблона)
- setter: split by `,`, trim each entry → сохранить в `AllowedValues`
- При изменении `_allowedValuesText` → вызвать RefreshValidation()

### 6. Обновление `ShareSettingsViewModel` (SmartCon.ProjectManagement)

**Файл:** `src/SmartCon.ProjectManagement/ViewModels/ShareSettingsViewModel.cs`

#### 6a. Рефакторинг Role → Field

Все `Role` → `Field` в свойствах, методах, логике.

#### 6b. Убрать `PredefinedRoles`

Заменить на `FieldLibrary`:
```csharp
// БЫЛО:
public IReadOnlyList<string> PredefinedRoles { get; } = FileBlockDefinition.PredefinedRoles.ToList();

// СТАЛО:
public ObservableCollection<FieldDefinition> FieldLibrary { get; } = [];
```

ComboBox для поля будет привязан к `FieldLibrary` (DisplayMemberPath = `DisplayName`).

#### 6c. Авто-парсинг при смене разделителя

**БЫЛО:** Кнопка «Разобрать имя файла» → команда `AutoParseCommand`.

**СТАЛО:** При изменении `Delimiter` — автоматически вызвать `AutoParseFileName()`.

```csharp
partial void OnDelimiterChanged(string value)
{
    AutoParseFileName();
    RefreshPreview();
}
```

`AutoParseFileName()`:
1. Split `CurrentFileName` по `Delimiter`
2. Обновить `Blocks.Count` (добавить/удалить строки)
3. Заполнить `CurrentFieldValue` каждого блока
4. Запустить валидацию → обновить `IsValid` и `ValidationError`

#### 6d. Валидация при изменении AllowedValues

```csharp
// В FileNameBlockItem:
partial void OnAllowedValuesTextChanged(string value) => RefreshValidation();
```

#### 6e. FieldLibrary CRUD

Новые команды:
```csharp
[RelayCommand] private void OpenFieldLibrary() { ... }
```

Открывает отдельное окно `FieldLibraryView`.

#### 6f. При выборе Field из библиотеки → автозаполнение AllowedValues

Когда пользователь выбирает поле в ComboBox (из FieldLibrary):
- Найти `FieldDefinition` по имени
- Подтянуть `AllowedValues` в `FileNameBlockItem.AllowedValuesText`

### 7. Новое окно: `FieldLibraryView` (SmartCon.ProjectManagement)

**Новые файлы:**
- `src/SmartCon.ProjectManagement/Views/FieldLibraryView.xaml`
- `src/SmartCon.ProjectManagement/Views/FieldLibraryView.xaml.cs`
- `src/SmartCon.ProjectManagement/ViewModels/FieldLibraryViewModel.cs`

**UI:**
```
┌──────────────────────────────────────────────────────────┐
│  Библиотека полей                                        │
│                                                          │
│  ┌─────────────┬──────────────┬────────────────────────┐ │
│  │ Имя поля    │ Отображение  │ Допустимые значения    │ │
│  ├─────────────┼──────────────┼────────────────────────┤ │
│  │ discipline  │ Дисциплина   │ AR, STR, MEP, CV       │ │
│  │ originator  │ Инициатор    │ ACME, BETA             │ │
│  │ status      │ Статус       │ S0, S1, S2, S3, S4     │ │
│  └─────────────┴──────────────┴────────────────────────┘ │
│  [+Добавить]  [-Удалить]  [Дублировать]                  │
│                                                          │
│                          [Сохранить]  [Отмена]           │
└──────────────────────────────────────────────────────────┘
```

**FieldLibraryViewModel:**
```csharp
public sealed partial class FieldLibraryViewModel : ObservableObject, IObservableRequestClose
{
    public ObservableCollection<FieldDefinitionItem> Fields { get; }

    [RelayCommand] private void AddField() { ... }
    [RelayCommand] private void RemoveField() { ... }
    [RelayCommand] private void DuplicateField() { ... }
    [RelayCommand] private void Save() { ... }

    public event Action? RequestClose;
}
```

**FieldDefinitionItem** (ViewModel item):
```csharp
public sealed partial class FieldDefinitionItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _allowedValuesText = string.Empty;  // comma-separated
}
```

**Стили:** `DialogWindowBase`, `SingletonResources`, стили из `Generic.xaml` (как ShareSettingsView).
**Локализация:** `{DynamicResource PM_*}` ключи.
**Размер:** 550×400, WindowStartupLocation=CenterOwner, Owner = Settings window.

### 8. Обновление вкладки «Именование» в XAML

**Файл:** `src/SmartCon.ProjectManagement/Views/ShareSettingsView.xaml`

#### 8a. DataGrid блоков — новые столбцы

**БЫЛО:** Pos | Role | Label

**СТАЛО:**

| Столбец          | Тип         | Привязка                    | Примечание                         |
|------------------|-------------|-----------------------------|------------------------------------|
| Pos (№)          | Text        | `Index`                     | ReadOnly                           |
| Field (Поле)     | ComboBox    | `Field`                     | ItemsSource = `FieldLibrary`       |
| Label (Метка)    | TextBox     | `Label`                     |                                    |
| Value (Значение) | Text        | `CurrentFieldValue`         | ReadOnly, auto из имени файла      |
| Status           | Text/Icon   | `IsValid` + `ValidationError` | DataTrigger: зелёный ✓ / красный ✗ |
| Allowed Values   | TextBox     | `AllowedValuesText`         | Comma-separated                    |

**Status столбец — DataTrigger:**
```xml
<DataGridTemplateColumn Header="..." Width="40">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <TextBlock FontSize="14" HorizontalAlignment="Center">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Setter Property="Text" Value="✓"/>
                        <Setter Property="Foreground" Value="Green"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsValid}" Value="False">
                                <Setter Property="Text" Value="✗"/>
                                <Setter Property="Foreground" Value="Red"/>
                                <Setter Property="ToolTip" Value="{Binding ValidationError}"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

#### 8b. Убрать кнопку «Разобрать имя файла»

Авто-разбор при смене разделителя. Кнопку и команду удалить.

#### 8c. Кнопка «Библиотека полей...»

Добавить кнопку рядом с [+Добавить] [-Удалить]:
```xml
<Button Content="{DynamicResource PM_FieldLibrary}" Command="{Binding OpenFieldLibraryCommand}"
        Style="{DynamicResource SecondaryButton}" Margin="4,0,0,0"/>
```

#### 8d. ComboBox для Field — привязка к FieldLibrary

```xml
<DataGridComboBoxColumn Header="..." SelectedItemBinding="{Binding Field}"
                        DisplayMemberPath="Name"
                        ItemsSource="{Binding DataContext.FieldLibrary, RelativeSource={RelativeSource AncestorType=DataGrid}}"/>
```

### 9. Обновление `ShareProjectCommand` (SmartCon.ProjectManagement)

**Файл:** `src/SmartCon.ProjectManagement/Commands/ShareProjectCommand.cs`

**БЫЛО:**
```csharp
var (isValid, error) = parser.Validate(originalDoc.Title, settings.FileNameTemplate);
if (!isValid)
{
    TaskDialog.Show("Share Project", error);
    return Result.Failed;
}
```

**СТАЛО:**
```csharp
var validation = parser.ValidateDetailed(originalDoc.Title, settings.FileNameTemplate);
if (!validation.IsValid)
{
    var details = string.Join("\n", validation.Blocks
        .Where(b => !b.IsValid)
        .Select(b => $"• {b.Field}: '{b.Value}' — {b.Error}"));
    TaskDialog.Show("Share Project",
        $"{LocalizationService.GetString("PM_Result_InvalidName")}\n\n{details}");
    return Result.Failed;
}
```

### 10. Сериализация (обратная совместимость)

**Файл:** `src/SmartCon.Core/Services/Storage/ShareSettingsJsonSerializer.cs`

`System.Text.Json` с `PropertyNameCaseInsensitive = true` автоматически десериализует:
- Старый JSON без `allowedValues` → `AllowedValues = []` (default)
- Старый JSON без `fieldLibrary` → `FieldLibrary = []` (default)
- Старый JSON с `role` → нужно добавить custom converter ИЛИ переименовать свойство с `JsonPropertyName`

**Решение для обратной совместимости:**

В `FileBlockDefinition` добавить:
```csharp
[JsonPropertyName("field")]
public string Field { get; init; } = string.Empty;

// Для чтения старых файлов где ключ был "role":
[JsonInclude, JsonPropertyName("role")]
internal string? RoleLegacy { get; init; }
```

Или проще: в `ShareSettingsJsonSerializer.Deserialize()` добавить post-processing:
```csharp
var settings = JsonSerializer.Deserialize<ShareProjectSettings>(json!, Options) ?? ShareProjectSettings.Empty;
// Миграция role → field для старых JSON
// System.Text.Json не сериализует [JsonExtensionData] в record легко,
// поэтому лучше использовать JsonNode для миграции
```

**Рекомендуемый подход:** Перед десериализацией — replace `"role":` → `"field":` в JSON строке.
Это наименее инвазивный способ:
```csharp
public static ShareProjectSettings Deserialize(string? json)
{
    if (string.IsNullOrWhiteSpace(json)) return ShareProjectSettings.Empty;
    // Миграция: старые JSON содержат "role", новые — "field"
    var migrated = json!.Replace("\"role\":", "\"field\":");
    return JsonSerializer.Deserialize<ShareProjectSettings>(migrated, Options)
           ?? ShareProjectSettings.Empty;
}
```

### 11. ExtensibleStorage (SmartCon.Revit)

**Файл:** `src/SmartCon.Revit/Storage/ProjectManagementSchema.cs`

Добавить поля для:
- `FieldLibrary` — JSON-строка (сериализовать List<FieldDefinition>)
- `AllowedValues` в каждом блоке — JSON-строка (List<string>)

**ИЛИ** — проще: сериализовать весь `ShareProjectSettings` в один JSON и хранить в одном поле
(как сейчас через `ShareSettingsJsonSerializer`).

### 12. Обновление `RevitViewRepository` (SmartCon.Revit)

**Файл:** `src/SmartCon.Revit/Sharing/RevitViewRepository.cs`

Без изменений в этом таске.

### 13. Локализация

**Файлы:**
- `src/SmartCon.UI/StringLocalization.cs` — новые ключи + значения RU/EN
- `src/SmartCon.Core/Services/LocalizationService.cs` — новые ключи

**Новые ключи:**

```csharp
// Field validation
public const string PM_Col_Field = "PM_Col_Field";               // Поле (было PM_Col_Role)
public const string PM_Col_Value = "PM_Col_Value";               // Значение
public const string PM_Col_Status = "PM_Col_Status";             // Статус
public const string PM_Col_AllowedValues = "PM_Col_AllowedValues"; // Допустимые значения
public const string PM_FieldLibrary = "PM_FieldLibrary";          // Библиотека полей...
public const string PM_Title_FieldLibrary = "PM_Title_FieldLibrary"; // Библиотека полей
public const string PM_Col_FieldName = "PM_Col_FieldName";       // Имя поля
public const string PM_Col_DisplayName = "PM_Col_DisplayName";   // Отображение
public const string PM_Col_Description = "PM_Col_Description";   // Описание
public const string PM_DuplicateField = "PM_DuplicateField";     // Дублировать
public const string PM_ValidField = "PM_ValidField";             // ✓
public const string PM_InvalidField = "PM_InvalidField";         // ✗
public const string PM_FieldValueNotAllowed = "PM_FieldValueNotAllowed"; // Value '{0}' not allowed for field '{1}'. Expected: {2}
```

**RU значения:**
```
PM_Col_Field = "Поле"
PM_Col_Value = "Значение"
PM_Col_Status = "Статус"
PM_Col_AllowedValues = "Допустимые значения"
PM_FieldLibrary = "Библиотека полей..."
PM_Title_FieldLibrary = "Библиотека полей"
PM_Col_FieldName = "Имя поля"
PM_Col_DisplayName = "Отображение"
PM_Col_Description = "Описание"
PM_DuplicateField = "Дублировать"
PM_ValidField = "✓"
PM_InvalidField = "✗"
PM_FieldValueNotAllowed = "Значение '{0}' недопустимо для поля '{1}'. Допустимые: {2}"
```

**EN значения:**
```
PM_Col_Field = "Field"
PM_Col_Value = "Value"
PM_Col_Status = "Status"
PM_Col_AllowedValues = "Allowed Values"
PM_FieldLibrary = "Field Library..."
PM_Title_FieldLibrary = "Field Library"
PM_Col_FieldName = "Field Name"
PM_Col_DisplayName = "Display Name"
PM_Col_Description = "Description"
PM_DuplicateField = "Duplicate"
PM_ValidField = "✓"
PM_InvalidField = "✗"
PM_FieldValueNotAllowed = "Value '{0}' is not allowed for field '{1}'. Expected: {2}"
```

### 14. Удалить `PM_Col_Role`

Заменить на `PM_Col_Field` везде — в StringLocalization, LocalizationService, XAML code-behind.

### 15. Тесты

**Файл:** `src/SmartCon.Tests/Core/Services/FileNameParserTests.cs`

Обновить все тесты:
- `Role` → `Field` во всех тестовых данных
- Удалить использование `FileBlockDefinition.PredefinedRoles`

**Новые тесты:**

```csharp
// ValidateDetailed — базовая валидация (без AllowedValues)
[Fact] void ValidateDetailed_NoRestrictions_AllBlocksValid()

// ValidateDetailed — с AllowedValues, все значения допустимы
[Fact] void ValidateDetailed_AllowedValues_AllMatch()

// ValidateDetailed — одно поле невалидно
[Fact] void ValidateDetailed_OneFieldInvalid()

// ValidateDetailed — несколько полей невалидны
[Fact] void ValidateDetailed_MultipleFieldsInvalid()

// ValidateDetailed — пустой AllowedValues = любое значение
[Fact] void ValidateDetailed_EmptyAllowedValues_AnyValueAccepted()

// ValidateDetailed — case-insensitive сравнение
[Fact] void ValidateDetailed_CaseInsensitive()

// Обратная совместимость JSON — "role" → "field"
[Fact] void Deserialize_LegacyJson_RoleMappedToField()

// FieldLibrary сериализация
[Fact] void SerializeDeserialize_FieldLibrary_RoundTrip()
```

---

## Порядок реализации

### Шаг 1: Рефакторинг Role → Field (все файлы)

1. `FileBlockDefinition.cs` — `Role` → `Field`, удалить `PredefinedRoles`
2. `FileNameBlockItem.cs` — `_role` → `_field`
3. `FileNameParser.cs` — все `Role` / `role` → `Field` / `field`
4. `IFileNameParser.cs` — без изменений сигнатур (Role не в интерфейсе)
5. `ShareSettingsViewModel.cs` — `PredefinedRoles` → `FieldLibrary`, все `Role` → `Field`
6. `ShareSettingsView.xaml` — столбец Role → Field, привязки
7. `ShareSettingsView.xaml.cs` — `ColRole` → `ColField`, header ключ
8. `ShareProjectCommand.cs` — `Role` → `Field`
9. `ShareSettingsJsonSerializer.cs` — миграция `role` → `field` при десериализации
10. Все тесты — `Role` → `Field`
11. `StringLocalization.cs` — `PM_Col_Role` → `PM_Col_Field`
12. `LocalizationService.cs` — обновить RU/EN значения
13. `ProjectManagementSchema.cs` — если используются field names с `role` → `field`
14. `docs/projectmanagement/naming-template.md` — обновить документацию

### Шаг 2: Новые модели

1. Создать `FieldDefinition.cs` в `SmartCon.Core/Models/`
2. Добавить `AllowedValues` в `FileBlockDefinition`
3. Добавить `FieldLibrary` в `ShareProjectSettings`
4. Обновить `ShareProjectSettings.Empty` если нужно

### Шаг 3: FileNameParser — ValidateDetailed

1. Создать `BlockValidation` и `ValidationResult` record'ы (можно в `FileNameParser.cs` или отдельный файл)
2. Добавить `ValidateDetailed` в `FileNameParser`
3. Обновить `IFileNameParser` интерфейс
4. Скомпилировать — убедиться что всё собирается

### Шаг 4: UI — автопарсинг + визуальная валидация

1. Обновить `FileNameBlockItem` — добавить `_allowedValuesText`, `_currentFieldValue`, `_isValid`, `_validationError`
2. Обновить `ShareSettingsViewModel`:
   - Заменить `PredefinedRoles` на `FieldLibrary` (ObservableCollection<FieldDefinition>)
   - Убрать кнопку «Разобрать имя файла» → автопарсинг в `OnDelimiterChanged`
   - Добавить `AutoParseFileName()` — split + fill blocks + validate
   - Загрузка `FieldLibrary` из настроек
   - При выборе Field из ComboBox → автозаполнение AllowedValues
3. Обновить `ShareSettingsView.xaml`:
   - DataGrid: добавить столбцы Value, Status, AllowedValues
   - Убрать кнопку «Разобрать имя файла»
   - Добавить кнопку «Библиотека полей...»
   - DataTrigger для Status столбца (зелёный/красный)
   - ComboBox ItemsSource = FieldLibrary

### Шаг 5: Окно FieldLibraryView

1. Создать `FieldDefinitionItem` ViewModel
2. Создать `FieldLibraryViewModel` с CRUD
3. Создать `FieldLibraryView.xaml` + `.xaml.cs`
4. В `ShareSettingsViewModel` — команда `OpenFieldLibraryCommand`
5. Передать FieldLibrary через диалог (как MappingEditorView)

### Шаг 6: ShareProjectCommand — новая валидация

1. Заменить `parser.Validate()` на `parser.ValidateDetailed()`
2. Форматировать детали ошибки для TaskDialog
3. Использовать `LocalizationService.GetString()` для заголовка

### Шаг 7: Сериализация + обратная совместимость

1. `ShareSettingsJsonSerializer.Deserialize()` — replace `"role":` → `"field":` в JSON
2. `FieldLibrary` сериализуется автоматически (System.Text.Json)
3. `AllowedValues` в блоках сериализуется автоматически
4. Обновить JSON-формат в `docs/projectmanagement/naming-template.md`

### Шаг 8: Локализация

1. Добавить новые ключи в `StringLocalization.cs` (Keys class + BuildRu/BuildEn)
2. Добавить новые ключи в `LocalizationService.cs` (Ru/En словари)

### Шаг 9: Тесты

1. Обновить все существующие тесты (Role → Field)
2. Добавить тесты для `ValidateDetailed`
3. Добавить тесты для JSON-миграции
4. Добавить тесты для `FieldLibrary` сериализации
5. Запустить `dotnet test` — все 700+ тестов должны пройти

### Шаг 10: Сборка + деплой

1. `dotnet build` — R25, R24, R21, R19 — 0 ошибок
2. `dotnet test` — 0 падений
3. `build-and-deploy.bat` — деплой во все версии Revit

---

## JSON-формат (итоговый)

```json
{
  "shareFolderPath": "\\\\server\\Project\\03_Shared\\",
  "syncBeforeShare": true,
  "fieldLibrary": [
    {
      "name": "discipline",
      "displayName": "Дисциплина",
      "description": "Код инженерной дисциплины",
      "allowedValues": ["AR", "STR", "MEP", "CV"]
    },
    {
      "name": "originator",
      "displayName": "Инициатор",
      "description": "Код компании-инициатора",
      "allowedValues": ["ACME", "BETA"]
    },
    {
      "name": "status",
      "displayName": "Статус",
      "description": "Статус документа по ISO 19650",
      "allowedValues": ["S0", "S1", "S2", "S3", "S4"]
    }
  ],
  "fileNameTemplate": {
    "delimiter": "-",
    "blocks": [
      { "index": 0, "field": "project",  "label": "Код проекта",    "allowedValues": [] },
      { "index": 1, "field": "originator","label": "Инициатор",     "allowedValues": ["ACME", "BETA"] },
      { "index": 2, "field": "volume",   "label": "Том",           "allowedValues": [] },
      { "index": 3, "field": "level",    "label": "Уровень",       "allowedValues": [] },
      { "index": 4, "field": "type",     "label": "Тип документа", "allowedValues": ["M", "S", "D"] },
      { "index": 5, "field": "discipline","label": "Дисциплина",    "allowedValues": ["AR", "STR", "MEP", "CV"] },
      { "index": 6, "field": "number",   "label": "Номер",         "allowedValues": [] },
      { "index": 7, "field": "status",   "label": "Статус",        "allowedValues": ["S0", "S1", "S2", "S3", "S4"] }
    ],
    "statusMappings": [
      { "wipValue": "S0", "sharedValue": "S1" },
      { "wipValue": "S1", "sharedValue": "S2" }
    ]
  },
  "purgeOptions": { ... },
  "keepViewNames": [ ... ]
}
```

---

## Проверочный чеклист

После реализации убедиться:

- [ ] Термин «Role» не встречается нигде в моделях Core, интерфейсах, ViewModel (кроме комментариев/документации)
- [ ] `FileBlockDefinition.PredefinedRoles` удалён
- [ ] Старый JSON с `"role":` корректно мигрирует при десериализации
- [ ] `ValidateDetailed` возвращает `ValidationResult` с детализацией по каждому блоку
- [ ] UI показывает зелёный ✓ для валидных полей, красный ✗ для невалидных
- [ ] Пустой `AllowedValues` = любое значение допустимо (не блокирует Share)
- [ ] Заполненный `AllowedValues` = только эти значения (блокирует Share при несовпадении)
- [ ] Авто-разбор при смене разделителя (без кнопки)
- [ ] Библиотека полей — отдельное окно с CRUD
- [ ] При выборе поля из библиотеки → AllowedValues подтягиваются в блок
- [ ] AllowedValues можно переопределить для конкретного блока (не зависит от библиотеки)
- [ ] Import/Export JSON содержит `fieldLibrary` + `allowedValues`
- [ ] TaskDialog при невалидном имени показывает детальный список ошибок
- [ ] Все 4 конфигурации собираются: R25, R24, R21, R19
- [ ] Все тесты проходят (700+)
- [ ] Inварианты I-01..I-13 не нарушены
