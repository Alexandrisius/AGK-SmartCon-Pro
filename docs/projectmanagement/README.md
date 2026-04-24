# ProjectManagement — Модуль управления шарингом проектов

> **Статус:** ✅ Реализован (Phase 11 завершена)
> **Последнее обновление:** 2026-04-25

## Назначение

Модуль автоматизирует перемещение Revit-модели из рабочей зоны (WIP) в зону Shared
по стандарту ISO 19650. Создаёт лёгкую «заглушку» для связей — максимально очищенный
файл, который смежники могут подключать как связь без потери данных координации.

**Целевой пользователь:** BIM-координатор (настраивает) → MEP-инженер (выполняет шаринг одной кнопкой).

---

## Реализованные подфазы

### 11A — Core: модели и интерфейсы ✅

- `ShareProjectSettings`, `FileNameTemplate`, `FileBlockDefinition`, `StatusMapping`, `PurgeOptions`
- `ShareProjectResult`, `ViewInfo`, `FieldDefinition`
- `IShareProjectSettingsRepository`, `IShareProjectService`, `IModelPurgeService`, `IFileNameParser`, `IViewRepository`
- `ShareSettingsJsonSerializer` (pure C#, System.Text.Json)

### 11B — Revit: ExtensibleStorage + реализации сервисов ✅

- `ProjectManagementSchema` (ExtensibleStorage schema descriptor)
- `RevitShareProjectSettingsRepository` (CRUD в DataStorage с JSON-миграцией)
- `RevitShareProjectService` (8-шаговый алгоритм Share)
- `RevitModelPurgeService` (12-категорийная очистка + PerformanceAdviser purge)
- `RevitFileNameParser` (парсинг/трансформация/валидация имён)
- `RevitViewRepository` (список видов из документа)

### 11C — UI: окно настроек (ShareSettingsView) ✅

- `ShareSettingsViewModel` (4 таба: Общие, Очистка, Виды, Нейминг)
- `FieldLibraryView` + `FieldLibraryViewModel` (CRUD библиотеки полей)
- `ParseRuleView` + `ParseRuleViewModel` (визуальный редактор правил, 5 режимов)
- `AllowedValuesView` + `AllowedValuesViewModel` (редактор допустимых значений)
- `ExportNameDialog` + `ExportNameDialogViewModel` (ручное переопределение имени)
- ShareSettingsView.xaml (TabControl, модальное, DialogWindowBase)

### 11D — UI: прогресс шаринга + интеграция ✅

- `ShareProgressViewModel` (прогресс-бар + статус)
- `ShareProgressView.xaml` (TopMost, немодальное)
- `ShareProjectCommand` (IExternalCommand)
- RibbonBuilder: панель ProjectManagement + 2 кнопки
- ServiceRegistrar: регистрация ~15 сервисов и фабрик

### 11E — Тесты и полировка ✅

- FileNameParserTests, ShareSettingsJsonSerializerTests, PurgeOptionsTests
- ShareSettingsViewModelTests (VM-тесты с моками)
- Локализация RU/EN (DynamicResource)
- build-and-deploy.bat — 0 ошибок на всех конфигурациях
- 716 тестов pass

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
