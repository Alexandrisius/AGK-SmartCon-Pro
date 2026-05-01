# FamilyManager UX and Information Architecture

## Цель документа

Документ описывает структуру интерфейса MVP для WPF/dockable panel в smartCon.

## UX Principle

FamilyManager должен ощущаться как рабочий каталог, а не как набор диалогов. Основной экран — список семейств с быстрым поиском, фильтрами и карточкой выбранного элемента.

## Entry Points

| Entry | Назначение |
| --- | --- |
| Ribbon → Family Manager | Открыть основную панель |
| Ribbon → Family Manager Settings | Настройки каталога |
| Context action внутри panel | Import, Reindex, Load, Edit metadata |

## MVP Layout

```text
+-------------------------------------------------------------+
| Search bar                                  Import Settings  |
+----------------------+----------------------+---------------+
| Filters              | Family grid/list     | Details card  |
| - Status             | - Name               | - Preview     |
| - Category           | - Category           | - Metadata    |
| - Tags               | - Status             | - Types       |
| - Manufacturer       | - Version            | - Actions     |
+----------------------+----------------------+---------------+
| Status bar: catalog path, item count, indexing state         |
+-------------------------------------------------------------+
```

## Primary Regions

### Search Bar

- text search;
- clear button;
- optional search mode later: Standard / FTS / Semantic;
- MVP uses Standard only.

### Filter Panel

MVP filters:

- Status;
- Category;
- Tags;
- Manufacturer;
- Source/provider.

### Results Grid

Columns:

- Name;
- Category;
- Status;
- Version;
- Manufacturer;
- Updated;
- Source.

Important smartCon rule: `DataGridColumn.Header` must be assigned programmatically because of I-12.

### Details Card

Sections:

- Preview;
- Name and status;
- file/version info;
- tags;
- description;
- types summary;
- parameters summary;
- actions.

### Actions

| Action | MVP |
| --- | --- |
| Load to Project | Да |
| Edit Metadata | Да |
| Reindex | Да |
| Open File Location | Да |
| Archive | Да |
| Compare Versions | Нет |
| Publish to Server | Нет |

## Screens

| Screen | Purpose |
| --- | --- |
| Main Catalog Panel | Основной workflow |
| Import Dialog | Файл/папка, storage mode |
| Import Progress | Batch progress + cancel |
| Family Details | Детальная карточка |
| Edit Metadata Dialog | Tags, description, status |
| Settings Dialog | DB path, cache path, behavior |
| Error Dialog | Recoverable errors |

## Empty States

| State | Message |
| --- | --- |
| First run | "Create your first local FamilyManager catalog" |
| Empty catalog | "Import a family file or folder to start" |
| No search results | "No families match current filters" |
| No active Revit document | "Open a project to load families" |
| Missing file | "The original family file is not available" |
| Legacy limited mode | "Some operations are unavailable in this Revit version" |

## Loading and Progress States

Long-running operations:

- folder import;
- metadata extraction;
- reindex;
- load family if Revit operation is slow;
- future batch operations.

UX requirements:

- show progress count;
- allow cancel where safe;
- keep UI responsive;
- log elapsed time;
- provide summary after completion.

## Dockable Panel Considerations

Dockable panel is new infrastructure for smartCon, so MVP must explicitly decide:

| Question | Recommended MVP decision |
| --- | --- |
| Use dockable panel immediately? | Да, если инфраструктура стабилизируется в Phase 12A/12C |
| Fallback if dockable risky? | Modal window with same ViewModel |
| VM lifetime | Application lifetime for dockable content |
| Active document changes | через `IRevitContext`/document refresh service |
| Revit API calls | through ExternalEvent if modeless |

## Localization

- UI strings go through existing localization services.
- Simple labels can use `DynamicResource`.
- DataGrid headers are assigned in code-behind after `InitializeComponent`.
- Code-behind must remain minimal and only handle view initialization/localization headers.

## Visual Style

- Use `SmartCon.UI` and existing SmartCon theme.
- No MaterialDesignThemes/MahApps/HandyControl in MVP.
- Avoid decorative UI.
- Status colors must not be the only indicator; use text badges.

