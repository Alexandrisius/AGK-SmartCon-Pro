# ADR-012: Per-project ExtensibleStorage для маппинга фитингов

**Статус:** accepted
**Дата:** 2026-04-19
**Supersedes:** [ADR-004](004-json-mapping-storage.md)

## Контекст

ADR-004 хранил ConnectorTypes и FittingMappingRules в едином файле
`%APPDATA%\AGK\SmartCon\connector-mapping.json`, который разделяется между
всеми проектами Revit, открытыми на одной машине. На практике это оказалось
неправильной моделью:

- **Разные проекты → разные наборы семейств фитингов.** Правила, привязанные
  к именам семейств из проекта A, бессмысленны в проекте B.
- **Совместная работа в команде.** При передаче `.rvt` другому инженеру
  глобальный JSON не копируется вместе с моделью — настройки теряются.
- **Нет аудита изменений.** Кто и когда правил маппинг — непонятно: файл
  не в Git, не привязан к модели.

Нужно привязать хранилище к конкретному `.rvt` так, чтобы настройки
путешествовали вместе с моделью.

## Решение

Перенести хранилище в **ExtensibleStorage DataStorage** текущего Revit
`Document` (в терминологии плана — «per-project storage»). Файл `.rvt`
становится единственным источником правды. Глобальный JSON в AppData больше
не читается автоматически; пользователь может импортировать его вручную
через кнопку **Импорт** в окне Settings.

### Schema

- **GUID:** `4A5C3E1F-6B2D-4E8A-9C7F-12D3E4F5A6B7` (зафиксирован, менять нельзя —
  потеря данных)
- **Имя:** `SmartConFittingMappingSchema`
- **VendorId:** `AGKSMARTCON` (min 4 символа, только буквы/цифры)
- **Поля:**
  - `SchemaVersion` (int) — версия формата пейлоада, текущая `1`
  - `Payload` (string) — JSON-строка того же формата, что ранее лежал в
    AppData (добавлено только поле `schemaVersion` на верхнем уровне)
- **AccessLevel:** `ReadAccess = Public`, `WriteAccess = Vendor` — читать
  могут другие плагины/скрипты для диагностики, писать — только SmartCon.

`DataStorage` один на документ, имя `SmartCon.FittingMapping` для удобного
поиска через FilteredElementCollector.

### Архитектура

| Слой | Класс | Назначение |
|---|---|---|
| Core | `SmartCon.Core.Services.Storage.FittingMappingJsonSerializer` | Чистый C# сериализатор (System.Text.Json), DTO-слой. Без зависимостей от Revit API → покрыт unit-тестами (I-09). |
| Core | `MappingPayload` (record) | Immutable snapshot: SchemaVersion + ConnectorTypes + MappingRules. |
| Revit | `SmartCon.Revit.Storage.FittingMappingSchema` | Инкапсулирует регистрацию Schema (GUID, имя, поля, AccessLevel). |
| Revit | `SmartCon.Revit.Storage.RevitFittingMappingRepository` | Реализация `IFittingMappingRepository`. Читает/пишет DataStorage через `ITransactionService`. |

DI: `services.AddSingleton<IFittingMappingRepository, RevitFittingMappingRepository>()`.
Document берётся через `IRevitContext.GetDocument()` на каждый вызов —
переключение проектов обрабатывается автоматически.

### Lifecycle

**Read (`GetConnectorTypes` / `GetMappingRules`) — read-only, без транзакций:**
1. `FilteredElementCollector → DataStorage` с валидной `Entity(schema)`.
2. Нет → вернуть `MappingPayload.Empty` (проект открывается с пустыми
   коллекциями).
3. Есть → десериализовать `Payload` → вернуть содержимое.
4. `JsonException` → лог error, вернуть Empty (проект не блокируется из-за
   corrupted payload; пользователь переинициализирует через Import или Save).

**Write (`SaveConnectorTypes` / `SaveMappingRules`):**
- Через `ITransactionService.RunInTransaction("SmartCon: Save Mapping", ...)`.
- Первый Save создаёт `DataStorage.Create(doc)` + `Name = "SmartCon.FittingMapping"`.
- Последующие Save обновляют существующую Entity.

### Импорт / Экспорт

Перенос настроек между проектами выполняется **только вручную** через две
кнопки в окне Settings:

- **Импорт** — `OpenFileDialog`. `InitialDirectory` по умолчанию =
  `%APPDATA%\AGK\SmartCon\` с выделенным `connector-mapping.json`, если файл
  существует. Это упрощает миграцию со старой версии SmartCon без
  автоматического поведения «из ниоткуда». Если папки нет — диалог
  открывается в `Documents`.
- **Экспорт** — `SaveFileDialog` с дефолтным именем `smartcon-mapping.json`.

Авто-миграция из AppData JSON **не делается** сознательно — чтобы
пользователь понимал, откуда взялись данные, и мог контролировать перенос
между проектами.

### UI

Окно Settings открывается модально (`view.ShowDialog()` + `Owner =
MainWindowHandle` Revit). В модальном режиме Save/Import/Export
выполняются прямо в `SettingsCommand.Execute` на Revit main thread через
`ITransactionService` — отдельный `IExternalEventHandler` не требуется
(I-01 соблюдён: мы уже в main thread).

## Последствия

### Плюсы

- Настройки привязаны к `.rvt` → переносятся вместе с моделью, работают
  в команде без ручной синхронизации AppData.
- Per-project изоляция — разные `.rvt` с разными семействами не конфликтуют.
- Чистая архитектура: сериализатор в Core покрыт unit-тестами, Revit API
  инкапсулирован в `SmartCon.Revit`.
- Нет скрытых автоматических действий (авто-миграции) → предсказуемый UX.

### Минусы

- Breaking change для пользователей: после обновления плагина нужно один
  раз нажать Импорт в каждом проекте, где нужны старые настройки.
  Митигация: диалог Импорта сразу открывается в AppData-папке со старым
  JSON → путь к переносу очевиден.
- Workshared-модели (central/local): `DataStorage` может потребовать прав
  на Workset. В рамках текущего ADR — best effort (`SmartConFailurePreprocessor`
  покажет предупреждение). Детальный разбор — при необходимости в отдельном ADR.
- Один GUID Schema на все версии. Смена формата потребует миграции
  `SchemaVersion` внутри того же пейлоада (поэтому поле предусмотрено сразу).

### Обратная совместимость

`FittingMappingJsonSerializer.Deserialize` принимает легаси JSON без
`schemaVersion` (значение 0 нормализуется к `CurrentVersion = 1`), а также
`PropertyNameCaseInsensitive = true` — старые файлы из AppData читаются
как есть через кнопку Импорт.

## Альтернативы

1. **Оставить JSON в AppData (ADR-004).** Отклонено — см. проблемы в
   разделе «Контекст».
2. **Авто-миграция при первом открытии проекта.** Отклонено — пользователь
   не поймёт, откуда взялись данные, особенно если AppData JSON уже
   устарел и несовместим с семействами нового проекта.
3. **SQLite внутри `.rvt`.** Избыточно для ~10-50 правил; ExtensibleStorage —
   штатный механизм Revit.
4. **Shared Parameters / Project Parameters.** Не предназначены для структурированных
   JSON-данных; ограничены поддерживаемыми типами полей.

## Multi-version

ExtensibleStorage API (`Schema`, `SchemaBuilder`, `Entity`,
`DataStorage.Create`) доступен во всех поддерживаемых SmartCon версиях
Revit (2019, 2021–2025). Никаких version-specific веток кода не требуется.

## Связанные инварианты

- **I-03:** Все записи — через `ITransactionService.RunInTransaction`.
- **I-05:** Между транзакциями храним только факт существования DataStorage;
  `Element`/`Entity` каждый раз получаем заново через `FilteredElementCollector`.
- **I-09:** Сериализатор и DTO — в Core, без Revit API. Schema и
  Repository — в `SmartCon.Revit`.
- **I-10:** `MappingEditorView.xaml.cs` — только `DataContext = viewModel`.
