# UI спецификация — ProjectManagement

> Загружать: при работе с Views и ViewModels модуля ProjectManagement.

---

## Окна

### 1. ShareSettingsView (модальное)

**Режим:** Модальное окно (`ShowDialog`), Owner = Revit main window handle.
Открывается по кнопке «Settings» на панели ProjectManagement.

**Размер:** 600×500, MinWidth=550, MinHeight=450, WindowStartupLocation=CenterScreen

**Структура:** TabControl с 4 вкладками + нижняя панель кнопок.

#### Таб 1: «Общие» (General)

```
┌──────────────────────────────────────────────────────────┐
│  Текущий файл                                            │
│  ┌──────────────────────────────────────────────────────┐│
│  │ Путь:  C:\Project\02_WIP\0001-PPR-001-...-S0.rvt    ││
│  │ Папка: C:\Project\02_WIP\                            ││
│  │ Имя:   0001-PPR-001-00001-01-AR-M3-S0.rvt           ││
│  └──────────────────────────────────────────────────────┘│
│                                                          │
│  Папка Shared                                            │
│  ┌──────────────────────────────────────┐ ┌──────────┐  │
│  │ \\server\Project\03_Shared\          │ │ Обзор... │  │
│  └──────────────────────────────────────┘ └──────────┘  │
│                                                          │
│  [x] Синхронизировать перед шарингом                     │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

**Элементы:**
- **Секция «Текущий файл»** (ReadOnly):
  - Label «Путь» — полный путь к файлу
  - Label «Папка» — директория файла
  - Label «Имя» — имя файла
  - Источник данных: `IRevitContext.GetDocument().PathName`
- **Папка Shared:**
  - TextBox привязанный к `ShareFolderPath` + кнопка «Обзор...» (FolderBrowserDialog)
- **CheckBox** «Синхронизировать перед шарингом» → `SyncBeforeShare`

**Биндинги VM:**
```csharp
[ObservableProperty] private string _currentFilePath = string.Empty;
[ObservableProperty] private string _currentFolder = string.Empty;
[ObservableProperty] private string _currentFileName = string.Empty;
[ObservableProperty] private string _shareFolderPath = string.Empty;
[ObservableProperty] private bool _syncBeforeShare = true;
```

#### Таб 2: «Очистка» (Purge)

```
┌──────────────────────────────────────────────────────────┐
│  Выберите элементы для удаления из Shared-файла:         │
│                                                          │
│  [x] RVT-связи                                          │
│  [x] CAD-импорты                                        │
│  [x] Растровые изображения                              │
│  [x] Облака точек                                       │
│  [x] Группы и типы групп                                │
│  [x] Сборки                                             │
│  [x] MEP-пространства                                   │
│  [x] Арматура (Rebar)                                   │
│  [x] Арматурные каркасы (Fabric Reinforcement)           │
│  [x] Неиспользуемые элементы (Purge)                     │
│                                                          │
│  [Все]  [Ни одного]                                     │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

**Биндинги VM:**
```csharp
[ObservableProperty] private bool _purgeRvtLinks = true;
[ObservableProperty] private bool _purgeCadImports = true;
[ObservableProperty] private bool _purgeImages = true;
[ObservableProperty] private bool _purgePointClouds = true;
[ObservableProperty] private bool _purgeGroups = true;
[ObservableProperty] private bool _purgeAssemblies = true;
[ObservableProperty] private bool _purgeSpaces = true;
[ObservableProperty] private bool _purgeRebar = true;
[ObservableProperty] private bool _purgeFabricReinforcement = true;
[ObservableProperty] private bool _purgeUnused = true;
```

**Команды:**
- `SelectAllCommand` — установить все CheckBox = true
- `DeselectAllCommand` — установить все CheckBox = false

#### Таб 3: «Виды» (Views)

```
┌──────────────────────────────────────────────────────────┐
│  Виды для сохранения в Shared-файле:                     │
│  ┌──────────────────────┐ ┌────────────────────────────┐ │
│  │ [Обновить список]    │ │                            │ │
│  └──────────────────────┘ │ (x) TASK_3D_Общий          │ │
│                           │ ( ) {3D}                    │ │
│                           │ (x) CORD_План_1_этажа       │ │
│                           │ ( ) Navisworks_Общий        │ │
│                           │ ( ) Лист 1 - A0             │ │
│                           │ (x) TASK_Разрез_А-А         │ │
│                           │ ...                         │ │
│                           └────────────────────────────┘ │
│  Выбрано: 3 из 45                                        │
└──────────────────────────────────────────────────────────┘
```

**Элементы:**
- Кнопка «Обновить список» — загружает виды из текущего документа через `IViewRepository`
- ListBox с CheckBox'ами — каждый элемент `ViewSelectionItem`
- Counter «Выбрано: X из Y»

**ViewSelectionItem:**
```csharp
public sealed class ViewSelectionItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    public string Name { get; init; }
    public ElementId Id { get; init; }
    public string ViewType { get; init; }
}
```

**Биндинги VM:**
```csharp
public ObservableCollection<ViewSelectionItem> Views { get; }
[ObservableProperty] private int _selectedCount;
```

**Команды:**
- `RefreshViewsCommand` — вызвать `IViewRepository.GetAllViews(doc)`, обновить коллекцию
- При открытии таба — автоматически загрузить виды если список пуст

**Важно:** Выбранные виды хранятся в настройках по имени (`string`), не по ElementId.
При загрузке — сопоставлять имена из настроек с именами видов в файле.

#### Таб 4: «Нейминг» (Naming)

```
┌──────────────────────────────────────────────────────────┐
│  Шаблон имени файла                                      │
│                                                          │
│  Разделитель: [-]                                        │
│                                                          │
│  Блоки имени:                                            │
│  ┌─────┬────────────┬─────────────────────────────┐     │
│  │  №  │ Роль       │ Метка                       │     │
│  ├─────┼────────────┼─────────────────────────────┤     │
│  │  0  │ project    │ Код проекта                 │     │
│  │  1  │ originator │ Инициатор                   │     │
│  │  2  │ volume     │ Том                         │     │
│  │  3  │ level      │ Уровень                     │     │
│  │  4  │ type       │ Тип документа               │     │
│  │  5  │ discipline │ Дисциплина                  │     │
│  │  6  │ number     │ Номер                       │     │
│  │  7  │ status     │ Статус                      │  *  ││
│  └─────┴────────────┴─────────────────────────────┘     │
│  [+Добавить]  [-Удалить]                     * = статус │
│                                                          │
│  Маппинг статусов:                                       │
│  ┌──────────────────┬──────────────────┐                │
│  │ WIP              │ Shared           │                │
│  ├──────────────────┼──────────────────┤                │
│  │ S0               │ S1               │                │
│  │ WIP              │ SHARED           │                │
│  └──────────────────┴──────────────────┘                │
│  [+Добавить]  [-Удалить]                                 │
│                                                          │
│  ┌─ Превью ──────────────────────────────────────────┐  │
│  │ Текущее:  0001-PPR-001-00001-01-AR-M3-S0.rvt     │  │
│  │ Shared:   0001-PPR-001-00001-01-AR-M3-S1.rvt     │  │
│  └───────────────────────────────────────────────────┘  │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

**Блоки имени — DataGrid:**
- Колонка «№» (ReadOnly) — индекс блока (0-based)
- Колонка «Роль» — ComboBox с предустановленными ролями:
  `project`, `originator`, `volume`, `level`, `type`, `discipline`, `number`, `status`, `milestone`, `custom`
- Колонка «Метка» — TextBox для отображаемого имени
- Маркер `*` у строки с role = "status" (только один блок может быть статусом)

**Маппинг статусов — DataGrid:**
- Колонка «WIP» — TextBox
- Колонка «Shared» — TextBox
- Не может быть двух строк с одинаковым WIP

**Превью:**
- Подсвечивается блок статуса в имени (жирный/цветом)
- Обновляется при изменении любого параметра

**FileNameBlockItem:**
```csharp
public sealed class FileNameBlockItem : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _label = string.Empty;
}
```

**StatusMappingItem:**
```csharp
public sealed class StatusMappingItem : ObservableObject
{
    [ObservableProperty] private string _wipValue = string.Empty;
    [ObservableProperty] private string _sharedValue = string.Empty;
}
```

**Биндинги VM:**
```csharp
[ObservableProperty] private string _delimiter = "-";
public ObservableCollection<FileNameBlockItem> Blocks { get; }
public ObservableCollection<StatusMappingItem> StatusMappings { get; }
[ObservableProperty] private string _previewCurrent = string.Empty;
[ObservableProperty] private string _previewShared = string.Empty;
```

**Команды:**
- `AddBlockCommand` / `RemoveBlockCommand`
- `AddStatusMappingCommand` / `RemoveStatusMappingCommand`
- Превью обновляется через `RefreshPreview()` при любом изменении

#### Нижняя панель (общая для всех табов)

```
┌──────────────────────────────────────────────────────────┐
│  [Импорт...]  [Экспорт...]      [Сохранить]  [Отмена]   │
└──────────────────────────────────────────────────────────┘
```

**Кнопки:**
- **Импорт...** — OpenFileDialog (JSON), десериализовать в ShareProjectSettings, заполнить все поля
- **Экспорт...** — SaveFileDialog (JSON), сериализовать текущие настройки
- **Сохранить** — валидация → `IShareProjectSettingsRepository.Save()` → `DialogResult = true`
- **Отмена** — `DialogResult = false`

---

### 2. ShareProgressView (немодальное, TopMost)

**Режим:** Немодальное окно, TopMost=true.
Открывается во время выполнения ShareProjectCommand.
Закрывается автоматически после завершения.

**Размер:** 450×130, MinWidth=450, MinHeight=130
WindowStartupLocation=CenterScreen, WindowStyle=ToolWindow (или ThreeDBorderWindow)

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│  Синхронизируем ваш проект...                            │
│                                                          │
│  ████████████████████░░░░░░░░░░░░░░░░░░░  45%           │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

**Биндинги VM:**
```csharp
[ObservableProperty] private string _statusText = "Подготовка...";
[ObservableProperty] private int _progressValue;
[ObservableProperty] private int _progressMaximum = 100;
```

**Обновление прогресса:** Через `Dispatcher.Invoke` из Revit main thread
(операция Share выполняется в ExternalCommand.Execute = main thread).
Каждый шаг алгоритма обновляет StatusText и ProgressValue.

**Шкала прогресса:**

| Шаг | Прогресс | Текст |
|---|---|---|
| Валидация | 0-5% | "Проверяем настройки..." |
| Sync | 5-15% | "Синхронизируем ваш проект..." |
| Временный проект | 15-25% | "Создаём временный проект..." |
| Detach | 25-40% | "Открываем с отсоединением от ФХ..." |
| Очистка | 40-65% | "Очищаем модель..." |
| SaveAs | 65-80% | "Сохраняем в зону Shared..." |
| Завершение | 80-100% | "Переоткрываем локальный файл..." |

---

## Базовый класс окон

Все окна наследуют от `DialogWindowBase` (SmartCon.UI.Controls) — как AboutView и MappingEditorView.

```csharp
// Reference: AboutView.xaml.cs, MappingEditorView.xaml.cs
public partial class ShareSettingsView : DialogWindowBase
{
    public ShareSettingsView(ShareSettingsViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
```

```xml
<!-- XAML: наследование от DialogWindowBase -->
<controls:DialogWindowBase x:Class="SmartCon.ProjectManagement.Views.ShareSettingsView"
        xmlns:controls="clr-namespace:SmartCon.UI.Controls;assembly=SmartCon.UI"
        xmlns:ui="clr-namespace:SmartCon.UI;assembly=SmartCon.UI"
        Title="{DynamicResource PM_Title_Settings}"
        Background="{DynamicResource BackgroundBrush}">
    <Window.Resources>
        <ui:SingletonResources/>
    </Window.Resources>
    <!-- ... -->
</controls:DialogWindowBase>
```

---

## Единый стиль SmartCon (из SmartCon.UI/Generic.xaml)

**Никаких индивидуальных стилей для модуля.** Все контролы используют стили из `Generic.xaml`:

| Элемент | Стиль из Generic.xaml | Пример использования |
|---|---|---|
| TabControl | `{DynamicResource FlatTabControl}` | Как в MappingEditorView |
| TabItem | `{DynamicResource FlatTabItem}` | Как в MappingEditorView |
| DataGrid | `{DynamicResource CompactDataGrid}` | Как в MappingEditorView |
| DataGridRow | `{DynamicResource CompactDataGridRow}` | — |
| DataGridCell | `{DynamicResource CompactDataGridCell}` | — |
| DataGridColumnHeader | `{DynamicResource CompactDataGridColumnHeader}` | — |
| Buttons (основные) | `{DynamicResource PrimaryButton}` | Сохранить, Share |
| Buttons (вторичные) | `{DynamicResource SecondaryButton}` | Отмена, Импорт |
| Buttons (акцент) | `{DynamicResource AccentButton}` | Экспорт |
| CheckBox | `{DynamicResource CompactCheckBox}` | Очистка, Sync |
| TextBox | `{DynamicResource CompactTextBox}` | Разделитель, пути |
| ComboBox | `{DynamicResource CompactComboBox}` | Роли блоков |
| ProgressBar | `{DynamicResource FlatProgressBar}` | Прогресс шаринга |
| ListBox | `{DynamicResource CompactListBox}` | Список видов |
| Background | `{DynamicResource BackgroundBrush}` | Все окна |
| Text | `{DynamicResource TextPrimaryBrush}` | Основной текст |
| Border | `{DynamicResource BorderAltBrush}` | Разделители |

**DataGridColumn.Header** — программно через `x:Name` в code-behind (I-12):
```csharp
// В ShareSettingsView.xaml.cs (после InitializeComponent)
ColIndex.Header = LocalizationService.GetString(StringLocalization.Keys.PM_Col_Index);
ColRole.Header = LocalizationService.GetString(StringLocalization.Keys.PM_Col_Role);
```

---

## Локализация

### Паттерн: LocalizationService + StringLocalization + LanguageManager

Полностью идентичен PipeConnect. Язык переключается из окна About (общее для всего SmartCon).

### Новые ключи для StringLocalization.Keys

Добавить в `src/SmartCon.PipeConnect/Services/StringLocalization.cs` (класс `Keys`):

```csharp
// ProjectManagement — ShareSettingsView
public const string PM_Title_Settings = "PM_Title_Settings";
public const string PM_Tab_General = "PM_Tab_General";
public const string PM_Tab_Purge = "PM_Tab_Purge";
public const string PM_Tab_Views = "PM_Tab_Views";
public const string PM_Tab_Naming = "PM_Tab_Naming";
public const string PM_CurrentFile = "PM_CurrentFile";
public const string PM_FilePath = "PM_FilePath";
public const string PM_Folder = "PM_Folder";
public const string PM_FileName = "PM_FileName";
public const string PM_SharedFolder = "PM_SharedFolder";
public const string PM_Browse = "PM_Browse";
public const string PM_SyncBefore = "PM_SyncBefore";
public const string PM_PurgeTitle = "PM_PurgeTitle";
public const string PM_PurgeRvtLinks = "PM_PurgeRvtLinks";
public const string PM_PurgeCadImports = "PM_PurgeCadImports";
public const string PM_PurgeImages = "PM_PurgeImages";
public const string PM_PurgePointClouds = "PM_PurgePointClouds";
public const string PM_PurgeGroups = "PM_PurgeGroups";
public const string PM_PurgeAssemblies = "PM_PurgeAssemblies";
public const string PM_PurgeSpaces = "PM_PurgeSpaces";
public const string PM_PurgeRebar = "PM_PurgeRebar";
public const string PM_PurgeFabric = "PM_PurgeFabric";
public const string PM_PurgeUnused = "PM_PurgeUnused";
public const string PM_SelectAll = "PM_SelectAll";
public const string PM_DeselectAll = "PM_DeselectAll";
public const string PM_ViewsTitle = "PM_ViewsTitle";
public const string PM_RefreshViews = "PM_RefreshViews";
public const string PM_SelectedCount = "PM_SelectedCount";
public const string PM_NamingTemplate = "PM_NamingTemplate";
public const string PM_Delimiter = "PM_Delimiter";
public const string PM_Blocks = "PM_Blocks";
public const string PM_StatusMappings = "PM_StatusMappings";
public const string PM_Preview = "PM_Preview";
public const string PM_Col_Index = "PM_Col_Index";
public const string PM_Col_Role = "PM_Col_Role";
public const string PM_Col_Label = "PM_Col_Label";
public const string PM_Col_Wip = "PM_Col_Wip";
public const string PM_Col_Shared = "PM_Col_Shared";
public const string PM_StatusMarker = "PM_StatusMarker";
public const string PM_CurrentName = "PM_CurrentName";
public const string PM_SharedName = "PM_SharedName";
public const string PM_AddBlock = "PM_AddBlock";
public const string PM_RemoveBlock = "PM_RemoveBlock";
public const string PM_AddMapping = "PM_AddMapping";
public const string PM_RemoveMapping = "PM_RemoveMapping";

// ProjectManagement — ShareProgressView
public const string PM_Title_Progress = "PM_Title_Progress";
public const string PM_Step_Validate = "PM_Step_Validate";
public const string PM_Step_Sync = "PM_Step_Sync";
public const string PM_Step_TempProject = "PM_Step_TempProject";
public const string PM_Step_Detach = "PM_Step_Detach";
public const string PM_Step_Purge = "PM_Step_Purge";
public const string PM_Step_Save = "PM_Step_Save";
public const string PM_Step_Finish = "PM_Step_Finish";

// ProjectManagement — TaskDialog результаты
public const string PM_Result_Success = "PM_Result_Success";
public const string PM_Result_Failed = "PM_Result_Failed";
public const string PM_Result_NoSettings = "PM_Result_NoSettings";
public const string PM_Result_InvalidName = "PM_Result_InvalidName";
public const string PM_Result_SyncFailed = "PM_Result_SyncFailed";
```

### Добавить RU/EN значения в два места:

1. `LocalizationService.cs` (Core) — словари `Ru` и `En`
2. `StringLocalization.cs` (PipeConnect) — методы `BuildRu()` и `BuildEn()`

### Использование в XAML

Все строки — через `{DynamicResource PM_*}`:
```xml
<TabItem Header="{DynamicResource PM_Tab_General}" Style="{DynamicResource FlatTabItem}">
<CheckBox Content="{DynamicResource PM_PurgeRvtLinks}" .../>
<Button Content="{DynamicResource PM_Browse}" Style="{DynamicResource SecondaryButton}"/>
```

### Использование в C# (TaskDialog и т.д.)

```csharp
var msg = LocalizationService.GetString(StringLocalization.Keys.PM_Result_Success);
TaskDialog.Show("Share Project", msg);
```

---

## MVVM-паттерн

Инвариант I-10. Все ViewModel наследуют `ObservableObject` из CommunityToolkit.Mvvm.
Свойства через `[ObservableProperty]`, команды через `[RelayCommand]`.

---

## Проблема прогресс-бара — решение

В старом плагине прогресс-бар не обновлялся — застревал на 10-20% хотя операция завершалась.
Причина: code-behind напрямую менял `ProgressBar.Value` без Dispatcher.

### Решение: Dispatcher.Invoke с Background приоритетом

ShareProjectCommand выполняется на Revit main thread (IExternalCommand.Execute).
WPF-окно немодальное. Обновление UI через Dispatcher:

```csharp
// В ShareProjectService — callback после каждого шага:
private void UpdateProgress(ShareProgressViewModel vm, string text, int value)
{
    System.Windows.Application.Current.Dispatcher.Invoke(
        System.Windows.Threading.DispatcherPriority.Background,
        new Action(() =>
        {
            vm.StatusText = LocalizationService.GetString(text);
            vm.ProgressValue = value;
        }));
}
```

**Почему это работает:** `DispatcherPriority.Background` (значение 4) ниже чем
`DispatcherPriority.Normal` (9) и `DispatcherPriority.Render` (7). Dispatcher
обработает pending render-операции (перерисовку ProgressBar) перед тем как
вернуться к нашему вызову. Это даёт WPF время на отрисовку даже если мы
находимся на main thread.

**Альтернатива:** `Dispatcher.BeginInvoke` — fire-and-forget, но менее предсказуемо.

---

## Диалоги

### FolderBrowserDialog (для ShareFolderPath)

Вызывается из VM через команду. Использовать `System.Windows.Forms.FolderBrowserDialog`
(доступен через Reference на System.Windows.Forms — уже есть в проекте).

### OpenFileDialog / SaveFileDialog (Import/Export JSON)

- Import: `OpenFileDialog` с фильтром `JSON files (*.json)|*.json`
- Export: `SaveFileDialog` с фильтром `JSON files (*.json)|*.json`, default name `smartcon-share-settings.json`
