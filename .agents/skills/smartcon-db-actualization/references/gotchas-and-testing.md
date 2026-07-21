# Готчи движка актуализации + ручное тестирование

Реальные ловушки, пойманные при строительстве движка (июль 2026). Читай ДО написания кода.

## Готчи

### 1. «Apply не гасит детект» = вечный pending

Каждый артефакт — свой маркер: записал → детект погас → возобновляемость бесплатно.
Если apply пишет не туда (другой version_id, другой scope), детект вечен: янтарь/гейт
никогда не гаснет. Тест `Apply_..._ClearsDetection` обязателен.

### 2. Openability — по ВСЕМ вариантам группы

Группа имеет варианты 2023 (здоров) + 2026 (pending). Если считать openable только по
pending-строкам — ошибочно решишь, что группа newer-only. Движок открывает 2023-вариант
и apply пишет на ВСЕ варианты → группа processable. SQL NOT EXISTS по всем вариантам
(см. шаблон), не HAVING по отфильтрованным.

### 3. Критерий брака атрибутов (#151/#153)

- `#151`: `storage_type='Double' AND status='Found' AND unit_type_id IS NULL` — старая
  экстракция не писала unit id. Текущая пишет — после ремонта детект гаснет.
- `#153`: `value_text='READERROR'`.
- Риск ложного срабатывания: безразмерные Double-параметры (специя Number) теоретически
  могут давать `unit_type_id=NULL` легитимно → вечный pending. На проде не подтвердилось
  (0 из 3632), но если увидишь вечный pending по атрибутам — это оно; сузить критерий
  (напр. требовать ещё raw-формат `value_text`).

### 4. net48 ограничения

- `IReadOnlySet<T>` НЕ существует → `IReadOnlyCollection<T>`.
- Default interface methods НЕ работают (рантайм) → все члены контракта обязательные,
  `RunFileFreePassAsync` у большинства = `Task.FromResult(0)`.
- Format specifiers `{x:F0}` в interpolated strings на net48 в HelixToolkit-файлах (CS1739,
  см. #97) — к движку не относится, но помни при сборках всех конфигов.

### 5. `Progress<T>` — гонка в тестах и двойной хоп в проде

- В координирующем коде (движок/координатор) НЕ используй `new Progress<T>` для конвертации
  прогресса — inline-класс `IProgress<T>` (синхронный вызов).
- В тестах НЕ полагайся на `new Progress<T>(list.Add)` — Post в xUnit context даёт гонку
  с assert'ом. Синхронный fake: `class SyncProgress<T> : IProgress<T> { List<T> }`.

### 6. XAML: вложенные markup-расширения — краш плагина

`Value="{Binding X, FallbackValue={loc:Loc Key}}"` — компилируется (!), но BAML-загрузчик
падает при создании панели и ВЕСЬ плагин не грузится ("Binding невозможно задать в
свойстве FallbackValue"). Правило: fallback'ы локализации — в VM (computed property
`=> YesText ?? LanguageManager.GetString(...) ?? "Да"`), в XAML только простой `{Binding}`.

### 7. Диалог: порядок флагов = дедлок

`IsSummaryVisible = true` ДО `CanClose = true` → auto-close/тесты видят выключенный Close
→ `DialogCompletion` никогда не завершается → весь `UpdateAsync` висит. Правило:
состояния команд (`CanClose`) — ПЕРЕД экранным флагом (`IsSummaryVisible`).

### 8. CA1708 и существующие ключи

Прежде чем добавить константу локализации — grep: `Btn_OK` уже существовал, мой `Btn_Ok`
сломал сборку (CA1708, отличие только регистром). RU/EN значения — в
`LocalizationService.Keys.*.cs`, константы — в `SmartCon.UI/StringLocalization.cs`.

### 9. ShowWarning ≠ styled

`ShowWarning` — системный `MessageBox` (инородный вид). Для гейтов/объяснений FamilyManager
— `ShowInfo` (styled OK-only диалог через `ConfirmationDialogViewModel.IsOkOnly`).

### 10. PowerShell-скрипты: кодировки и here-strings

- `"""` — это C#, в PowerShell НЕ работает. Только `@" ... "@`, закрывающий `"@` строго в колонке 0.
- Кириллица в аргументах командной строки ломается кодировкой — имена айтемов в паттернах
  давай ASCII (`PPR-%`) или фильтруй от противного (`NOT LIKE 'PPR-%'`).
- Multi-statement `CommandText` работает через `;` (Microsoft.Data.Sqlite).

## Ручное тестирование: tools/damage-catalog-db.ps1

«Состаривает» тестовую БД ровно по критериям детекции (только loadable, только активная версия):

```powershell
# Все повреждения, все айтемы
pwsh tools/damage-catalog-db.ps1 -DbPath "<путь>\catalog.db"
# Только N айтемов (контрольная группа цела)
pwsh tools/damage-catalog-db.ps1 -DbPath "..." -Limit 3
# Только GLB и хэш
pwsh tools/damage-catalog-db.ps1 -DbPath "..." -Glb -Hash
# Сухой прогон + ожидаемый pending тем же SQL, что в задачах
pwsh tools/damage-catalog-db.ps1 -DbPath "..." -WhatIf
```

Флаги: `-Attributes` (runs+типы+значения, форма #152) · `-BreakUnits` (#151:
unit_type_id=NULL+raw) · `-ReadError` (#153) · `-Glb` · `-Hash` · `-Counters`.

Важно:
1. **Отключи базу в плагине / закрой Revit** перед скриптом (SQLite один writer).
2. Для newer-only сценария — руками SQL: `UPDATE catalog_versions SET revit_major_version = NNNN ...`
   (см. коммит-сессию июля 2026: E1001 → 2026 для critical newer-only, кириллические → 2025
   для optional newer-only/янтаря).
3. После прогона «Обновить базу» сверяй базу read-only SQL (pending=0 по критериям) —
   скрипт печатает ожидаемое N тем же SQL, что в задачах.
4. Новую задачу — добавь флаг в скрипт (та же форма: ломаем критерий) и её SQL в финальную
   проверку. Скрипт — зеркало детектов, держи их синхронными.
