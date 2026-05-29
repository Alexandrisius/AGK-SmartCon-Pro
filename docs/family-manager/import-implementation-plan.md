# FamilyManager Import Implementation Plan v3

> **Статус:** Концепция  
> **Дата:** 2026-05-29  
> **Фокус:** Batch Import без Deep Scan, Stale marker, инкремент/перезапись

---

## 1. Концепция

**Простота:** Нет ES GUID, нет Deep Scanner, нет версионирования Revit в путях.

**Идентификация:**
- Семейство = имя файла (normalized_name в БД)
- Версия = SHA256 (уникальный хэш содержимого файла)

**Batch Dialog:**
- Показывает ТОЛЬКО быстрые данные без открытия семейства (BasicFileInfo: версия Revit, имя файла, SHA256)
- НЕ сканирует типы/параметры/категорию через OpenDocumentFile
- После кнопки "Загрузить" → открываем семейство и извлекаем типы + атрибуты в БД через существующий экстрактор

**Stale Marker:**
- В БД хранится `current_version_label` для каждого семейства
- При импорте новой версии (инкремент) — автоматически обновляется `current_version_label`
- Пользователи видят статус "Устарело" если загруженная версия != current_version_label
- При перезаписи текущей версии (без инкремента) — stale НЕ приходит

---

## 2. Матрица бизнес-кейсов

| # | Сценарий | SHA256 | Имя | В БД? | Статус | Действие по умолчанию |
|---|----------|--------|-----|-------|--------|----------------------|
| 1 | **Новое семейство** | Новый | Не найдено | Нет | Новое | Инкремент версии (создать v1) |
| 2 | **Дубликат** | Совпадает | Любое | Да | Дубликат | Пропустить (авто) |
| 3 | **Обновление** | Другой | Найдено | Да | Существующее | Инкремент версии (по умолчанию) |
| 4 | **Мелкая правка** | Другой | Найдено | Да | Существующее | Перезаписать текущую (вручную) |
| 5 | **Ошибка файла** | — | — | — | Ошибка | Пропустить |

**Правила:**
1. SHA256 exact match → Дубликат (авто-skip, не показываем в диалоге или показываем серым)
2. Имя не найдено в БД → Новое
3. Имя найдено + SHA256 другой → Существующее (по умолчанию Инкремент)

---

## 3. Режимы импорта в Batch Dialog

| Режим | Что делает | Когда использовать |
|-------|-----------|-------------------|
| **Инкремент версии** (default) | Создаёт vN+1, новый файл, обновляет current_version_label | Любое обновление семейства |
| **Перезаписать текущую** | Заменяет файл в текущей версии (v1), НЕ меняет current_version_label | Мелкая правка, не требует stale у пользователей |
| **Пропустить** | Ничего | Авто для дубликатов |

---

## 4. Stale Marker (маркер устаревания)

### Механизм

```
При импорте семейства (Инкремент версии):
  1. Создаётся новая версия vN+1
  2. Обновляется catalog_items.current_version_label = "vN+1"
  3. У всех пользователей при Refresh — статус "Устарело" если их версия != current_version_label

При импорте семейства (Перезаписать текущую):
  1. Файл в текущей версии заменяется
  2. current_version_label НЕ меняется
  3. У пользователей статус НЕ меняется (так как версия та же)
```

### Отображение в UI
- Дерево семейств: иконка/цвет "Устарело" рядом с именем
- Статус обновляется по кнопке Refresh или при любом действии в FM
- Нет необходимости сканировать файлы в проекте — только сравнение версий в БД

---

## 5. Хранение файлов

```
files/{catalogItemId}/{versionLabel}/{fileName}
```

- Нет подпапки `r{revitVersion}`
- `revit_major_version` в БД только для информации

---

## 6. Pipeline импорта

```
[Кнопка "Импорт файлов"]
  ↓
[OpenFileDialog — Multiselect .rfa]
  ↓
[Быстрый анализ файлов] (async I/O, без Revit API)
  • SHA256
  • Имя файла
  • Версия Revit (BasicFileInfo.Extract)
  • Размер файла
  ↓
[Batch Dialog]
  • Показывает файлы со статусом (Новое / Существующее / Дубликат)
  • Пользователь выбирает категорию и режим (Инкремент / Перезапись / Пропустить)
  • По умолчанию: Инкремент для новых и существующих
  ↓
[Кнопка "Загрузить"]
  ↓
[ExternalEvent — импорт]
  Для каждого файла:
    • CopyToStorage (без r{version})
    • Обновить БД (новая версия или перезапись)
    • Если Инкремент → обновить current_version_label (trigger stale для пользователей)
    • Открыть семейство через OpenDocumentFile()
    • Извлечь типоразмеры + атрибуты через существующий экстрактор
    • Сохранить в БД (family_types, extracted_attribute_values)
  ↓
[Закрыть диалог, обновить дерево]
```

---

## 7. Что УДАЛЯЕМ

```
❌ FamilyCatalogSchema/Storage        — ES GUID не нужен
❌ IFamilyDeepScanner / RevitFamilyDeepScanner  — не нужен (нет сканирования перед диалогом)
❌ family_stable_guid в БД            — не нужен
❌ r{revitVersion} в путях            — не нужен
❌ ImportFolderAsync                  — заменён на мультиселект
❌ "Импорт данных" команда            — переносится в загрузку семейства
❌ AddRevitVersion действие           — не нужно
```

---

## 8. Что СОЗДАЁМ / МЕНЯЕМ

### Новые файлы
```
SmartCon.Core/Models/FamilyManager/FamilyBatchImportItem.cs      # Row data
SmartCon.Core/Models/FamilyManager/FamilyBatchImportAction.cs    # Enum: Increment, Overwrite, Skip
SmartCon.Core/Models/FamilyManager/FamilyBatchImportStatus.cs    # Enum: New, Existing, Duplicate, Error
SmartCon.Core/Models/FamilyManager/FamilyBatchImportActionOption.cs  # Display wrapper
SmartCon.FamilyManager/ViewModels/FamilyBatchImportViewModel.cs  # Dialog VM
SmartCon.FamilyManager/ViewModels/FamilyBatchImportRow.cs        # Row VM
SmartCon.FamilyManager/Views/FamilyBatchImportView.xaml          # Dialog UI (стиль Settings/ShareProject)
```

### Изменённые файлы
```
StoragePathResolver.cs             # Убрать r{version}
FamilyCatalogSql.cs                # Убрать revit_version из UNIQUE
LocalFamilyImportService.cs        # Инкремент / Перезапись, без ES
LocalFamilyImportService.Database.cs # UpdateVersion, FindByName
FamilyManagerMainViewModel.Import.cs # BatchDialog для Import + UpdateFamily
FamilyManagerMainViewModel.Tree.cs   # Stale marker отображение
ServiceRegistrar.cs                # +BatchImport dialog
StringLocalization.cs              # Локализация Batch Dialog
LocalizationService.Keys.FamilyManager.cs  # RU/EN строки
```

---

## 9. Дедупликация

```csharp
1. Вычислить SHA256
2. Поискать SHA256 в БД (family_files.sha256)
   → Найден? → Дубликат (Skip)
3. Нормализовать имя файла
4. Поискать normalized_name в catalog_items
   → Найдено? → Существующее (по умолчанию Инкремент)
   → Не найдено? → Новое (по умолчанию Инкремент)
```

---

## 10. Этапы реализации

### Этап 1: Убрать r{version} из путей (0.5 ч)
- [ ] StoragePathResolver — убрать revitMajorVersion

### Этап 2: Batch Dialog UI (2 ч)
- [ ] FamilyBatchImportView — стиль Settings/ShareProject (таблица, кнопки, прогресс)
- [ ] FamilyBatchImportViewModel / Row — данные, команды
- [ ] Локализация всех строк

### Этап 3: ImportService — Инкремент и Перезапись (2 ч)
- [ ] ImportBatchAsync — switch по режимам
- [ ] IncrementVersionAsync — новая версия, обновление current_version_label
- [ ] OverwriteCurrentAsync — замена файла, БЕЗ изменения current_version_label
- [ ] Дедупликация по SHA256

### Этап 4: Интеграция экстрактора (1.5 ч)
- [ ] При импорте: после CopyToStorage → открыть семейство → извлечь типы + атрибуты
- [ ] Использовать существующий сервис экстракции (IFamilyDataExtractionService)
- [ ] Удалить старую команду "Импорт данных"

### Этап 5: Stale Marker (1 ч)
- [ ] Добавить поле/логику для current_version_label
- [ ] Обновлять при Инкременте, НЕ обновлять при Перезаписи
- [ ] Отображать в дереве (иконка/цвет)
- [ ] Обновление по Refresh

### Этап 6: ViewModel + DI (1 ч)
- [ ] ImportFilesAsync → BatchDialog (multiselect)
- [ ] UpdateFamilyAsync → BatchDialog (forced Existing)
- [ ] DI регистрация

### Этап 7: Тесты (1 ч)
- [ ] Инкремент версии
- [ ] Перезапись текущей
- [ ] Дедупликация SHA256
- [ ] Stale marker

**Итого: ~9 часов**

---

## 11. Критерии готовности

- [ ] Batch Dialog в стиле Settings/ShareProject (таблица, кнопки, локализация)
- [ ] Данные в Batch Dialog без OpenDocumentFile (только BasicFileInfo)
- [ ] Режимы: Инкремент версии (default), Перезаписать текущую, Пропустить
- [ ] Дедупликация по SHA256 (авто-skip)
- [ ] Инкремент создаёт vN+1 и обновляет current_version_label
- [ ] Перезапись заменяет файл, НЕ меняет current_version_label
- [ ] Stale marker отображается в дереве при устаревшей версии
- [ ] Типы и атрибуты извлекаются при импорте (через существующий экстрактор)
- [ ] Команда "Импорт данных" удалена
- [ ] Импорт папки удалён, мультиселект работает
- [ ] Все unit-тесты проходят
