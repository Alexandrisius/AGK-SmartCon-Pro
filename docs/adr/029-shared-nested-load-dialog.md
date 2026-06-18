# ADR-029: Shared nested families — user dialog for load mode (Issue #67)

**Status:** accepted
**Date:** 2026-06-17
**Branch:** feature/issue-67-shared-nested-dialog
**Issue:** [#67](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/67)

## Контекст

При загрузке семейства из **Family Manager** в проект, если у загружаемого
семейства есть **общие вложенные семейства** (shared nested), плагин через
`IFamilyLoadOptions.OnSharedFamilyFound` всегда возвращал
`FamilySource.Family` + `overwriteParameterValues = true`.

В больших проектах (тысячи shared nested) это приводило к:

1. **Массовой перезаписи** всех shared nested в проекте на новые версии.
2. **Многосекундным задержкам** (5-10 минут в больших моделях).
3. **Встроенным диалогам Revit** «Use the existing / Use the loaded version»
   для каждого конфликтующего семейства (issue #67).

Тот же hardcoded `FamilySource.Family + overwrite=true` использовался и в
**PipeConnect CTC writeback** (`RevitFamilyConnectorService`,
`CtcFamilyWriter`). Хотя CTC writeback меняет только описание коннектора,
Revit всё равно вызывал `OnSharedFamilyFound` для shared nested, которые
были загружены в проект и изменились в `.rfa` — что могло приводить к
перезаписи shared nested в проекте без необходимости.

## Решение

### 1. FamilyManager — пользовательский диалог с 3 вариантами

При срабатывании `OnSharedFamilyFound` (т.е. Revit обнаружил shared nested
**в проекте** и он **изменился** — гарантия из Revit API docs) показываем
наш WPF-диалог с тремя radio-button:

| Choice | API mapping | Описание |
|---|---|---|
| `UseProject` | `source = Project`, `overwrite = false` | Безопасный — оставить как есть |
| `OverwriteParameters` | `source = Family`, `overwrite = true` | Заменить параметры существующих типов |
| `OverwriteAll` | `source = Family`, `overwrite = true` | Полная перезапись (текущее поведение) |

Диалог показывается **ровно столько раз, сколько shared nested нужно
обновить**. Каждый раз — отдельное решение для каждого вложенного.

### 2. PipeConnect CTC writeback — безопасный дефолт без диалога

В обоих местах CTC writeback (`RevitFamilyConnectorService.cs:414-432`
и `CtcFamilyWriter.cs:367-386`) hardcoded `overwrite=true` для shared
nested заменён на `FamilySource.Project` + `overwrite=false`. Это
безопасно: CTC writeback меняет **только** описание коннектора в основном
семействе, shared nested **не трогает**.

Если Revit всё-таки вызовет `OnSharedFamilyFound` (например, когда в
проекте уже была более новая версия shared nested, чем в familyDoc
после `EditFamily`), теперь будет использоваться проектная версия —
никакого диалога пользователю в PipeConnect не показывается.

### Архитектурные изменения

#### Core модели (новые)

```
src/SmartCon.Core/Models/FamilyManager/
├── SharedFamiliesLoadChoice.cs          (enum: UseProject / OverwriteParameters / OverwriteAll)
└── SharedFamilyDecisionRequest.cs        (record: SharedFamilyName, IsFamilyInUse, ParentFamilyName)
```

#### Расширения интерфейсов (back-compat)

```csharp
// IFamilyLoadService — добавлен опциональный параметр onSharedDecision
Task<FamilyLoadResult> LoadFamilyAsync(
    FamilyResolvedFile file, FamilyLoadOptions options,
    Action<string>? onStatusMessage = null,
    Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
    CancellationToken ct = default);

// IFamilyManagerDialogService — новый метод
SharedFamiliesLoadChoice ShowSharedFamiliesLoadModeDialog(SharedFamilyDecisionRequest request);
```

#### Revit реализация

- `RevitFamilyLoadOptions` — хранит `Func<,>?` callback, вызывает его
  синхронно в `OnSharedFamilyFound` (блокирует Revit main thread на время
  диалога — допустимо согласно Jeremy Tammik blog 0302).
- `RevitFamilyLoadService` — пробрасывает callback из VM в `IFamilyLoadOptions`.

#### WPF диалог (новый)

```
src/SmartCon.FamilyManager/
├── Views/SharedFamiliesLoadModeDialogView.xaml      (460×Auto, 3 RadioButton, предупреждение для in-use)
├── Views/SharedFamiliesLoadModeDialogView.xaml.cs  (5 строк, I-10)
└── ViewModels/SharedFamiliesLoadModeDialogViewModel.cs  (ObservableObject + IObservableRequestClose)
```

Стиль соответствует остальным диалогам SmartCon: цвета
`WarningBrush`/`AccentBrush`, кнопки `PrimaryButton`/`SecondaryButton`,
локализация через `LanguageManager`.

#### Локализация

10 новых ключей `FM_LoadShared_*` в `StringLocalization.cs` и
`LocalizationService.Keys.FamilyManager.cs` (ru + en).

#### Тесты

3 набора unit-тестов:

- `SharedFamiliesLoadModeDialogViewModelTests` — VM логика (default choice, Apply/Cancel, radio-binding).
- `FamilyManagerDialogServiceSharedNestedTests` — мок `IDialogPresenter`, проверка возврата cancel→UseProject.
- `SharedFamilyDecisionRequestTests` — record equality.

### Решение для back-compat

Все существующие callers `IFamilyLoadService.LoadFamilyAsync` без
`onSharedDecision` продолжают работать с **дефолтным** поведением
(`FamilySource.Family` + `overwrite = true`) — никаких регрессий.

## Альтернативы (рассмотренные, но не выбранные)

1. **Предварительный анализ `.rfa` через `ExtractPartAtomFromFamilyFile`**
   или `Family.GetFamilyTypeParameterValues` для определения списка
   shared nested до загрузки. Отвергнуто: пользователь явно попросил
   показывать диалог только когда Revit реально сообщает о конфликте
   через `OnSharedFamilyFound`.

2. **Чекбокс «Apply to all»** — применить выбранный режим ко всем
   оставшимся shared nested без повторных диалогов. Отвергнуто:
   пользователь хочет видеть диалог для каждого вложенного отдельно.

3. **Кэширование выбора между сессиями** в `%APPDATA%`. Отвергнуто:
   не запрошено; добавим если появится жалоба.

4. **Использовать нативный Revit диалог** через `RevitUIFamilyLoadOptions`.
   Отвергнуто: пользователь явно хочет «наш стиль, локализация, цвета».

## Источники

- Jeremy Tammik blog 0302, 0597, 1214, 0199 — IFamilyLoadOptions паттерны.
- Autodesk forum: REVIT-198137 — баг с null sharedFamily в старых Revit.
- Autodesk forum (Thomas LECUPPRE) — `source = default; overwrite = false` = Project.
- Revit API docs OnSharedFamilyFound — «Triggered only when the family is both loaded and changed».

## Следствие

- **Pos:** пользователь получает контроль над каждым конфликтующим shared
  nested. Безопасный дефолт «Use Project» исключает случайную перезапись.
- **Pos:** PipeConnect CTC writeback больше не трогает shared nested —
  меньше неожиданных изменений в проекте при CTC-операциях.
- **Neg:** диалог появляется N раз подряд для семейств с N shared nested.
  Для больших проектов это может раздражать. Mitigated: диалог маленький,
  default = Use Project, Enter = Apply.
- **Neg:** в Revit ≤ 2024.2 параметр `sharedFamily` в
  `OnSharedFamilyFound` приходит как parent family (баг REVIT-198137).
  Mitigated: наш диалог показывает `sharedFamily?.Name` — даже если это
  parent, пользователь всё равно может сделать выбор; в имени будет
  parent, и пользователь увидит это явно.
