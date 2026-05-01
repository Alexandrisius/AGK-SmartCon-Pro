# ADR-016: ReadOnly-флаг для managed-файлов семейств

**Status:** accepted
**Date:** 2026-04-30

## Context

FamilyManager копирует `.rfa` файлы в managed storage при импорте. Эти файлы являются авторитетными копиями (source of truth) — пользователь не должен изменять их напрямую через проводник или сторонние инструменты. Все изменения должны проходить через FamilyManager.

## Decision

### FM-016: Все managed-файлы получают атрибут ReadOnly

При копировании `.rfa` в managed storage устанавливается Windows-атрибут `FileAttributes.ReadOnly`:

```csharp
File.Copy(sourcePath, absolutePath, overwrite: true);
File.SetAttributes(absolutePath, File.GetAttributes(absolutePath) | FileAttributes.ReadOnly);
```

### Правило для кода, изменяющего managed-файлы

Любой код, который перезаписывает, удаляет или модифицирует managed-файл, **обязан**:

1. **Снять** `ReadOnly` перед операцией:
   ```csharp
   File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
   ```
2. **Выполнить** операцию (copy, delete, move)
3. **Установить** `ReadOnly` обратно после успешной операции:
   ```csharp
   File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
   ```

Это касается:
- Обновления семейства (re-import новой версии)
- Миграции файлов между версиями
- Любых фоновых операций с managed storage

**Исключение:** удаление файла не требует установки ReadOnly обратно.

## Consequences

- Пользователь не сможет случайно перезаписать или изменить managed-файл через проводник
- Revit может читать ReadOnly-файлы без ограничений (LoadFamily не требует записи)
- Код, работающий с managed storage, должен учитывать флаг при любых файловых операциях
- Встроен в `LocalFamilyImportService.ImportFileAsync()` при начальном импорте
