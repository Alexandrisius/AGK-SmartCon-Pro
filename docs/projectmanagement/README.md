# ProjectManagement — Модуль управления шарингом проектов

> **Статус:** Планирование
> **Последнее обновление:** 2026-04-23

## Назначение

Модуль автоматизирует перемещение Revit-модели из рабочей зоны (WIP) в зону Shared
по стандарту ISO 19650. Создаёт лёгкую «заглушку» для связей — максимально очищенный
файл, который смежники могут подключать как связь без потери данных координации.

**Целевой пользователь:** BIM-координатор (настраивает) → MEP-инженер (выполняет шаринг одной кнопкой).

---

## Промпт-план для AI-агента

> **Инструкция:** Этот раздел — пошаговый план реализации модуля.
> Выполняй подфазы последовательно. Каждая подфаза имеет критерии приёмки.
> После завершения подфазы — запусти `build-and-deploy.bat` и тесты.

### Подфаза 11A: Core — модели и интерфейсы

**Цель:** Создать все модели данных и интерфейсы в SmartCon.Core.

**Шаги:**
1. Создать 7 моделей в `SmartCon.Core/Models/`:
   - `ShareProjectSettings.cs` — корневая модель (ShareFolderPath, FileNameTemplate, PurgeOptions, KeepViewNames, SyncBeforeShare)
   - `FileNameTemplate.cs` — шаблон (Delimiter, Blocks, StatusMappings)
   - `FileBlockDefinition.cs` — блок (Index, Role, Label)
   - `StatusMapping.cs` — пара (WipValue, SharedValue)
   - `PurgeOptions.cs` — 10 boolean-флагов очистки
   - `ShareProjectResult.cs` — результат (Success, SharedFilePath, ElapsedSeconds, ErrorMessage, ElementsDeleted, PurgedElementsCount)
   - `ViewInfo.cs` — (Name, ElementId Id, ViewType)
2. Создать 5 интерфейсов в `SmartCon.Core/Services/Interfaces/`:
   - `IShareProjectSettingsRepository` — Load/Save/ExportToJson/ImportFromJson
   - `IShareProjectService` — Share(settings) → ShareProjectResult
   - `IModelPurgeService` — Purge(doc, options, keepViewNames) → int
   - `IFileNameParser` — TransformStatus/Validate/ParseBlocks
   - `IViewRepository` — GetAllViews(doc) → List<ViewInfo>
3. Создать сериализатор `SmartCon.Core/Services/Storage/ShareSettingsJsonSerializer.cs`:
   - Serialize(ShareProjectSettings) → string
   - Deserialize(string) → ShareProjectSettings
   - Формат JSON описан в `naming-template.md`
4. Добавить ключи локализации в `LocalizationService` (RU + EN словари) для строк модуля
5. Обновить `docs/domain/models.md` и `docs/domain/interfaces.md`
6. Создать unit-тесты:
   - `FileNameParserTests.cs` — тесткейсы в `naming-template.md`
   - `ShareSettingsJsonSerializerTests.cs` — serialize/deserialize roundtrip
   - `PurgeOptionsTests.cs` — default values

**Критерии приёмки:**
- Core компилируется на R25 + R21 без ошибок
- I-09 соблюдён (Core не вызывает Revit API, нет using System.Windows)
- Все unit-тесты pass
- Модели добавлены в `docs/domain/models.md`

### Подфаза 11B: Revit — ExtensibleStorage и реализации

**Цель:** Создать все Revit-зависимые реализации.

**Шаги:**
1. Создать `SmartCon.Revit/Storage/ProjectManagementSchema.cs`:
   - Новый GUID (уникальный, не совпадающий с FittingMappingSchema)
   - SchemaName = "SmartConProjectManagementSchema"
   - DataStorageName = "SmartCon.ProjectManagement"
   - Поля: SchemaVersion (int), Payload (string)
   - AccessLevel = Public (как в FittingMappingSchema — VendorId "AGK" < 4 символов)
2. Создать `SmartCon.Revit/Storage/RevitShareProjectSettingsRepository.cs`:
   - Паттерн идентичен RevitFittingMappingRepository
   - Чтение: FilteredElementCollector → DataStorage → Entity → Payload → Deserialize
   - Запись: через ITransactionService.RunInTransaction
   - ExportToJson/ImportFromJson: делегируют ShareSettingsJsonSerializer
3. Создать `SmartCon.Revit/Sharing/RevitModelPurgeService.cs`:
   - Алгоритм в `share-algorithm.md` (шаг 5)
   - Каждая категория — отдельный приватный метод
   - Purge через PerformanceAdviser GUID в цикле до стабилизации
   - IFailuresPreprocessor через ITransactionService
4. Создать `SmartCon.Revit/Sharing/RevitFileNameParser.cs`:
   - Алгоритм в `naming-template.md`
5. Создать `SmartCon.Revit/Sharing/RevitViewRepository.cs`:
   - FilteredElementCollector.OfClass(typeof(View)) → List<ViewInfo>
   - Исключить шаблоны (v.IsTemplate == false)
6. Создать `SmartCon.Revit/Sharing/RevitShareProjectService.cs`:
   - 8-шаговый алгоритм из `share-algorithm.md`
   - IShareProjectService.Share(settings) → ShareProjectResult

**Критерии приёмки:**
- Revit компилируется на R25 + R21
- I-03 (транзакции через сервис), I-05 (не хранить Element), I-07 (FailuresPreprocessor)
- Все новые классы sealed

### Подфаза 11C: UI — окно настроек

**Цель:** Создать ShareSettingsView (модальное, 4 таба).

**Шаги:**
1. Создать ViewModels:
   - `ShareSettingsViewModel.cs` — 4 группы свойств + команды
   - `ViewSelectionItem.cs` — ObservableObject (IsSelected, Name, Id, ViewType)
   - `FileNameBlockItem.cs` — ObservableObject (Index, Role, Label)
   - `StatusMappingItem.cs` — ObservableObject (WipValue, SharedValue)
2. Создать `ShareSettingsView.xaml`:
   - наследовать от `DialogWindowBase` (как AboutView)
   - `<ui:SingletonResources/>` в Window.Resources
   - `LanguageManager.EnsureWindowResources(this)` в code-behind
   - Background=`{DynamicResource BackgroundBrush}`
   - TabControl с `FlatTabControl` / `FlatTabItem` (из SmartCon.UI Generic.xaml)
   - DataGrid с `CompactDataGrid` стилями
   - Buttons с `PrimaryButton` / `SecondaryButton` / `AccentButton`
   - Все строки через `{DynamicResource ...}` (ключи в StringLocalization)
3. Создать `ShareSettingsCommand.cs`:
   - Модальное окно: `view.ShowDialog()`
   - Owner = Revit main window handle (как в SettingsCommand PipeConnect)
4. Создать фабрики:
   - `IShareSettingsViewModelFactory.cs` + `ShareSettingsViewModelFactory.cs`

**Критерии приёмки:**
- Окно открывается модально из Ribbon
- Все стили из SmartCon.UI (Generic.xaml) — единый визуал
- I-10: code-behind только DataContext + EnsureWindowResources + BindCloseRequest
- Локализация через DynamicResource

### Подфаза 11D: UI — прогресс шаринга + Ribbon

**Цель:** Завершить модуль — прогресс-бар, Ribbon, интеграция.

**Шаги:**
1. Создать `ShareProgressViewModel.cs`:
   - Properties: StatusText, ProgressValue
   - Команда Cancel (опционально)
2. Создать `ShareProgressView.xaml`:
   - `DialogWindowBase`, TopMost, WindowStyle=ToolWindow
   - ProgressBar `FlatProgressBar`, Label, Минимальный размер 450×130
3. Создать `ShareProjectCommand.cs`:
   - IExternalCommand.Execute():
     a. Load settings from IShareProjectSettingsRepository
     b. Validate via IFileNameParser
     c. Show ShareProgressView (немодальное)
     d. Call IShareProjectService.Share(settings)
     e. Update progress via Dispatcher (см. "Проблема прогресс-бара" ниже)
     f. Show TaskDialog with result
     g. Close progress window
4. Обновить `SmartCon.App/Ribbon/RibbonBuilder.cs`:
   - Новая панель "ProjectManagement" на вкладке SmartCon
   - Кнопка "ShareProject" → ShareProject_32x32.png / ShareProjectCommand
   - Кнопка "Settings" → Settings_32x32.png / ShareSettingsCommand
5. Обновить `SmartCon.App/DI/ServiceRegistrar.cs`:
   - Регистрация всех новых сервисов и фабрик
6. Добавить ключи локализации в `StringLocalization.cs` (Keys-класс + BuildRu/BuildEn)

**Критерии приёмки:**
- Полный цикл: Settings → Share → файл в зоне Shared
- Имя файла трансформировано (статус WIP→Shared)
- Прогресс-бар обновляется корректно (до 100%)
- Ribbon: новая панель с 2 кнопками + иконками
- build-and-deploy.bat: 0 ошибок на R25, R21, R19

### Подфаза 11E: Тесты и полировка

**Шаги:**
1. Unit-тесты: FileNameParserTests, ShareSettingsJsonSerializerTests, PurgeOptionsTests
2. ViewModel-тесты: ShareSettingsViewModelTests (с моками IShareProjectSettingsRepository, IViewRepository)
3. Проверить локализацию: все DynamicResource ключи определены в RU и EN
4. `dotnet test` — 0 падений
5. Обновить docs/: models.md, interfaces.md, solution-structure.md, roadmap.md (статус → Готов)

---

## Бизнес-кейсы

### BC-01: Координатор настраивает плагин первый раз

**Контекст:** Новый проект, координатор создал файл в папке WIP.
**Действия:**
1. Координатор нажимает Settings на панели ProjectManagement
2. В табе «Общие» видит путь текущего файла
3. Нажимает «Обзор» и выбирает папку Shared (абсолютный путь)
4. В табе «Нейминг» задаёт разделитель «-», описывает блоки (8 блоков), указывает блок 7 = status
5. Добавляет пару маппинга: S0 → S1
6. Нажимает «Экспорт» → сохраняет `smartcon-share-settings.json`
7. Нажимает «Сохранить»

**Результат:** Настройки сохранены в ExtensibleStorage текущего файла.
JSON-файл можно импортировать в другие файлы проекта.

### BC-02: Координатор переносит настройки в другой файл

**Контекст:** В проекте несколько .rvt файлов (AR, ME, ST).
**Действия:**
1. Координатор открывает файл ME-дисциплины
2. Нажимает Settings
3. Нажимает «Импорт» → выбирает `smartcon-share-settings.json` из файла AR
4. Папка Shared автоматически подхватывается (или меняет если нужно)
5. В табе «Виды» нажимает «Обновить список» → выбирает виды для ME
6. Нажимает «Сохранить»

**Результат:** Настройки из AR скопированы, виды обновлены для ME.

### BC-03: Инженер шарит проект одной кнопкой

**Контекст:** Настройки уже созданы координатором.
**Действия:**
1. Инженер нажимает «Share Project» на панели ProjectManagement
2. Появляется окно прогресса: «Синхронизируем...» → «Очищаем...» → «Сохраняем...»
3. Прогресс-бар доходит до 100%
4. Появляется TaskDialog: «Проект перемещён в зону Shared. Путь: \\server\03_Shared\...-S1.rvt. Время: 45 сек.»
5. Локальный файл переоткрыт и синхронизирован

**Результат:** В папке Shared появился очищенный файл с именем *-S1.rvt.
Локальный файл продолжает работу.

### BC-04: Ошибка — настройки не созданы

**Контекст:** Инженер нажимает Share Project без настройки.
**Действия:**
1. ShareProjectCommand загружает настройки → пустые
2. TaskDialog: «Сначала настройте параметры шаринга через кнопку Settings»
3. Операция прерывается

### BC-05: Ошибка — имя файла не соответствует шаблону

**Контекст:** Файл переименован вручную и не парсится.
**Действия:**
1. IFileNameParser.Validate → error = "Имя файла содержит 5 блоков, а шаблон ожидает 8"
2. TaskDialog с конкретной ошибкой
3. Операция прерывается

### BC-06: Ошибка — Sync не удался

**Контекст:** Центральный файл недоступен.
**Действия:**
1. SyncWithoutRelinquishing бросает исключение
2. TaskDialog: «Синхронизация не удалась: {message}. Продолжить без синхронизации?»
3. Если «Да» — продолжаем с текущим состоянием локального файла
4. Если «Нет» — операция прерывается

### BC-07: Standalone файл (не workshared)

**Контекст:** Файл не привязан к центральному.
**Действия:**
1. Проверить `doc.IsWorkshared`
2. Если false — пропустить шаг DetachFromCentral
3. Скопировать файл напрямую, очистить, сохранить в Shared
4. (Алгоритм упрощается: нет нужды в временном проекте для переключения)

---

## Карта документации модуля

| Документ | Описание | Когда загружать |
|---|---|---|
| [share-algorithm.md](share-algorithm.md) | Алгоритм операции Share, шаги, обработка ошибок | При реализации ShareProjectService |
| [ui-spec.md](ui-spec.md) | Спецификация UI: окна, вкладки, биндинги, локализация | При работе с UI |
| [naming-template.md](naming-template.md) | Парсер имён файлов, блоки, роли, маппинг статусов | При реализации FileNameParser |
| [field-validation-plan.md](field-validation-plan.md) | План: валидация полей + рефакторинг Role→Field | При добавлении AllowedValues и FieldLibrary |

---

## Ключевые решения

| Решение | Выбор | Обоснование |
|---|---|---|
| Хранение настроек | ExtensibleStorage в .rvt (ADR-013) | Настройки привязаны к файлу, путешествуют с моделью |
| Категории очистки | Настраиваемый список (PurgeOptions) | Разные проекты требуют разного уровня очистки |
| Виды для сохранения | Динамический список из файла | Координатор выбирает конкретные виды |
| Нейминг файлов | Кастомный разделитель + гибкая привязка блоков | Универсальность под любой стандарт |
| Статусы | Пары WIP→Shared (StatusMapping) | Расширяемо: S0→S1, WIP→SHARED и т.д. |
| Модуль | Отдельный csproj SmartCon.ProjectManagement | Изоляция по аналогии с PipeConnect |
| Ribbon | Отдельная панель ProjectManagement | Визуальное разделение модулей |
| UI-стили | Единые стили из SmartCon.UI/Generic.xaml | Нет индивидуальностей — единый SmartCon |
| Локализация | LocalizationService + StringLocalization + LanguageManager | Язык меняется из About (как в PipeConnect) |
| Прогресс-бар | Dispatcher обновление из Revit main thread | Проблема старого плагина решена корректно |

---

## Проблема прогресс-бара (из старого плагина)

В старом ShareProject прогресс-бар **не обновлялся** — застревал на 10-20% хотя операция завершалась.
Причина: code-behind напрямую менял `ProgressBar.Value` из основного потока Revit,
но WPF-окно не перерисовывалось пока thread занят длительной операцией.

**Решение в новом модуле:**

ShareProjectCommand выполняется на Revit main thread (IExternalCommand.Execute).
ShareProgressView — немодальное окно. Чтобы UI обновлялся во время длительных операций:

```csharp
// В ShareProjectService после каждого шага:
System.Windows.Application.Current.Dispatcher.Invoke(
    () => { progressVm.StatusText = "Очищаем модель..."; progressVm.ProgressValue = 45; },
    System.Windows.Threading.DispatcherPriority.Background);
```

`Dispatcher.Invoke` с `Background` приоритетом позволяет WPF обработать очередь рендеринга
между шагами алгоритма, даже когда мы находимся на main thread.
Альтернатива: `Dispatcher.BeginInvoke` для асинхронного обновления.

**Важно:** Не использовать `Thread.Sleep` или `DoEvents` — только Dispatcher.

---

## Связанные инварианты

- **I-01:** Все вызовы Revit API из WPF — через ExternalEvent (ShareProgressView немодальное)
- **I-03:** Транзакции только через ITransactionService
- **I-05:** Не хранить Element/Connector между транзакциями
- **I-07:** IFailuresPreprocessor для подавления warnings при удалении
- **I-09:** Core не вызывает Revit API. Модели + интерфейсы + сериализация — pure C#
- **I-10:** MVVM строго. .xaml.cs только DataContext + EnsureWindowResources + BindCloseRequest
- **I-12:** DataGridColumn.Header — программно через x:Name

---

## Паттерны переиспользования из PipeConnect

Модуль ProjectManagement следует тем же паттернам что и PipeConnect.
При реализации — смотреть на существующий код как на reference:

| Что | Reference в PipeConnect | Аналог в ProjectManagement |
|---|---|---|
| ExtensibleStorage | `FittingMappingSchema.cs` + `RevitFittingMappingRepository.cs` | `ProjectManagementSchema.cs` + `RevitShareProjectSettingsRepository.cs` |
| Модальное окно | `MappingEditorView.xaml` + `MappingEditorView.xaml.cs` | `ShareSettingsView.xaml` + `ShareSettingsView.xaml.cs` |
| ViewModel фабрика | `ISettingsViewModelFactory` + `SettingsViewModelFactory` | `IShareSettingsViewModelFactory` + `ShareSettingsViewModelFactory` |
| JSON сериализация | `FittingMappingJsonSerializer.cs` | `ShareSettingsJsonSerializer.cs` |
| Command | `SettingsCommand.cs` | `ShareSettingsCommand.cs` |
| DialogWindowBase | `AboutView.xaml.cs` | `ShareSettingsView.xaml.cs`, `ShareProgressView.xaml.cs` |
| Локализация | `StringLocalization.cs` (Keys + BuildRu/BuildEn) | Добавить ключи PM_* в тот же StringLocalization |
| DI регистрация | `ServiceRegistrar.cs` | Добавить новые сервисы в тот же файл |
