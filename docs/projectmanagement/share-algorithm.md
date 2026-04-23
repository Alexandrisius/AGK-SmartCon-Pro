# Алгоритм Share Project

> Загружать: при реализации `IShareProjectService` и `IModelPurgeService`.

---

## Обзор операции

Операция Share создаёт лёгкую копию текущего Revit-файла в зоне Shared.
Копия detached от центрального файла, очищена от лишних элементов и
содержит только минимальный набор данных для использования как связь смежниками.

---

## Шаги алгоритма

### Шаг 1: Валидация

```
1.1. Загрузить настройки из IShareProjectSettingsRepository
1.2. Если настройки пустые → показать ошибку "Сначала настройте параметры шаринга"
1.3. Проверить что файл сохранён (doc.PathName не пустой)
1.4. Парсинг имени файла через IFileNameParser:
     - Разбить по разделителю на блоки
     - Найти блок с ролью "status"
     - Проверить что текущее значение блока есть в WipValue одного из StatusMappings
1.5. Если валидация провалена → TaskDialog с конкретной ошибкой, return Failed
```

### Шаг 2: Синхронизация локального файла

```
2.1. Если settings.SyncBeforeShare == true:
     - SyncWithoutRelinquishing(doc)
       (TransactWithCentralOptions + SynchLockCallback + SynchronizeWithCentralOptions)
     - Обработать исключение: показать TaskDialog, предложить продолжить без sync
```

### Шаг 3: Создание временного проекта

```
3.1. app.NewProjectDocument(UnitSystem.Metric) — пустой проект без шаблона
3.2. Сохранить как {локальная_папка}\file_temp.rvt (SaveAs с OverwriteExistingFile)
3.3. uiapp.OpenAndActivateDocument(tempDoc.PathName) — переключить активный вид
```

**Зачем:** Revit позволяет закрыть документ только если переключились на другой.
Временный проект — «мост» для переключения.

### Шаг 4: Detach from central

```
4.1. Получить путь центрального файла: doc.GetWorksharingCentralModelPath()
4.2. Закрыть локальный файл: doc.Close(false) — можно, т.к. активный вид на temp проекте
4.3. Открыть центральный с опциями:
     - DetachFromCentralOption.DetachAndPreserveWorksets
     - Audit = true
     - AllowOpeningLocalByWrongUser = true
4.4. Получить документ detach-копии: uiapp.ActiveUIDocument.Document
```

### Шаг 5: Очистка модели (IModelPurgeService)

Очищаем detach-копию по настройкам PurgeOptions. Выполняется внутри одной транзакции
с IFailuresPreprocessor для подавления warnings при удалении.

```
5.1. ITransactionService.RunInTransaction("ShareProject: Purge", doc => {

  // A. Разгруппировка (если PurgeGroups)
  foreach (Group g in collector.OfClass(typeof(Group)))
      g.UngroupMembers();

  // B. Разборка сборок (если PurgeAssemblies)
  foreach (AssemblyInstance a in collector.OfClass(typeof(AssemblyInstance)))
      a.Disassemble();

  // C. Удаление по категориям (каждый в try/catch — пропускать неудаляемое)
  // C1. GroupType (если PurgeGroups)
  // C2. MEP Spaces (если PurgeSpaces)
  // C3. Views — кроме keepViewNames (ВСЕГДА: фильтр по имени)
  // C4. RVT Links (если PurgeRvtLinks)
  // C5. Raster Images (если PurgeImages)
  // C6. CAD Imports (если PurgeCadImports)
  // C7. Point Clouds (если PurgePointClouds)
  // C8. Rebar (если PurgeRebar)
  // C9. Fabric Reinforcement (если PurgeFabricReinforcement)

  // D. Purge неиспользуемых (если PurgeUnused)
  //     PerformanceAdviser rule GUID: "e8c63650-70b7-435a-9010-ec97660c1bda"
  //     Цикл до стабилизации:
  //     while (GetPurgeableElements() возвращает элементы) {
  //         doc.Delete(purgeable);
  //     }
});
```

### Шаг 6: Вычисление пути Shared

```
6.1. Трансформировать имя файла через IFileNameParser:
     - Заменить блок статуса: WipValue → SharedValue
     - Пример: "0001-PPR-001-00001-01-AR-M3-S0.rvt" → "0001-PPR-001-00001-01-AR-M3-S1.rvt"
6.2. Путь: settings.ShareFolderPath + "\" + transformedFileName
6.3. Проверить что ShareFolderPath существует, если нет — создать
```

### Шаг 7: Сохранение в Shared

```
7.1. SaveAsOptions:
     - OverwriteExistingFile = true
     - WorksharingSaveAsOptions: SaveAsCentral = true
7.2. detachSharedModel.SaveAs(modelPathOut, options)
```

### Шаг 8: Завершение

```
8.1. Закрыть detach-копию: detachSharedModel.Close(false)
8.2. Закрыть временный проект: tempDoc.Close(false)
8.3. Удалить временный файл: File.Delete(tempPath)
8.4. Открыть локальный файл:
     - OpenOptions с DetachFromCentralOption.DoNotDetach
     - AllowOpeningLocalByWrongUser = true
8.5. SyncWithoutRelinquishing (локальный файл)
8.6. Показать TaskDialog с результатом:
     - Путь к Shared файлу
     - Время операции
     - Количество удалённых элементов
```

---

## Обработка ошибок

| Шаг | Возможная ошибка | Обработка |
|---|---|---|
| 1 | Настройки пустые | TaskDialog + return Failed |
| 1 | Имя файла не парсится | TaskDialog с деталями + return Failed |
| 2 | Sync failed | TaskDialog с вопросом «Продолжить без синхронизации?» |
| 3 | Временный файл не создаётся | TaskDialog + return Failed |
| 4 | Central file недоступен | TaskDialog + return Failed |
| 5 | Ошибка при удалении элемента | try/catch → continue (пропустить элемент) |
| 7 | Нет прав на запись в Shared | TaskDialog + откат (закрыть detach, переоткрыть local) |
| 8 | Локальный файл не открывается | TaskDialog — критическая ошибка |

---

## Категории очистки — детали Revit API

### Удаление видов (фильтр)

```csharp
var viewsToDelete = new FilteredElementCollector(doc)
    .OfClass(typeof(View))
    .Cast<View>()
    .Where(v => !v.IsTemplate)
    .Where(v => !keepViewNames.Contains(v.Name))
    .Select(v => v.Id)
    .ToList();
```

**Важно:** Не удалять виды-шаблоны (`v.IsTemplate`) — они могут использоваться
оставленными видами. Не удалять виды которые используются в листах (проверка через
`FilteredElementCollector` на `Viewport`).

### Purge неиспользуемых (PerformanceAdviser)

```csharp
const string PurgeGuid = "e8c63650-70b7-435a-9010-ec97660c1bda";

// Найти правило по GUID
var ruleId = PerformanceAdviser.GetPerformanceAdviser()
    .GetAllRuleIds()
    .First(id => id.Guid.ToString() == PurgeGuid);

// Цикл до стабилизации
int totalDeleted = 0;
while (true)
{
    var messages = PerformanceAdviser.GetPerformanceAdviser()
        .ExecuteRules(doc, new[] { ruleId });
    
    if (messages.Count == 0) break;
    
    var elementIds = messages[0].GetFailingElements().ToList();
    if (elementIds.Count == 0) break;
    
    doc.Delete(elementIds);
    totalDeleted += elementIds.Count;
}
```

**Почему цикл:** После удаления одной партии неиспользуемых элементов,
другие элементы могут стать неиспользуемыми (cascade-эффект).
Цикл гарантирует полную очистку.

### Облака точек (PointCloudInstance)

```csharp
var pointClouds = new FilteredElementCollector(doc)
    .OfClass(typeof(PointCloudInstance))
    .Select(v => v.Id)
    .ToList();
```

### Группы и сборки

```csharp
// Сначала разгруппировать (создаёт элементы внутри группы как самостоятельные)
foreach (var g in new FilteredElementCollector(doc)
    .OfClass(typeof(Group)).Cast<Group>())
    g.UngroupMembers();

// Затем удалить GroupType
var groupTypes = new FilteredElementCollector(doc)
    .OfClass(typeof(GroupType))
    .Select(v => v.Id);

// Сборки — разобрать
foreach (var a in new FilteredElementCollector(doc)
    .OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>())
    a.Disassemble();
```

---

## SyncWithoutRelinquishing

```csharp
var transOpts = new TransactWithCentralOptions();
transOpts.SetLockCallback(new SynchLockCallback()); // ShouldWaitForLockAvailability = true

var syncOpts = new SynchronizeWithCentralOptions();
syncOpts.SetRelinquishOptions(new RelinquishOptions(false)); // не отдавать элементы
syncOpts.SaveLocalAfter = true;
syncOpts.Comment = "SmartCon - ShareProject";

doc.SynchronizeWithCentral(transOpts, syncOpts);
```

---

## Временные ресурсы

Создаётся один временный файл:
- `{локальная_папка}\file_temp.rvt`

Обязательно удалить в блоке finally или при откате.
