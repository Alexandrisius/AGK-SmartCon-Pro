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

## Exception: OverwriteCurrent (ADR-040)

`OverwriteCurrent` — явное действие пользователя через batch dialog (комбо-бокс
"Перезаписать текущую версию"), НЕ silent change. Оно нарушает базовый принцип
"изменения = новая версия", но оправдано UX-сценарием: пользователь внёс
незначительные правки в семейство и не хочет плодить новые версии.

При `OverwriteCurrent` managed-файл текущей версии перезаписывается по тому же
пути (`{catalogItemId}/{currentVersionLabel}/`), а запись в `catalog_versions`
UPDATE (а не INSERT новой строки):

| Слой | Операция |
|---|---|
| `.rfa/.rvt` файл | Снять ReadOnly → SaveAs с `OverwriteExistingFile=true` → установить ReadOnly |
| `catalog_versions` | UPDATE `content_hash`, `hash_format_version`, `types_count`, `parameters_count`, `published_at_utc` WHERE `id = currentVersionId`. `id`, `version_label`, `revit_major_version` НЕ меняются |
| `family_files` | UPDATE `file_name`, `imported_at_utc` WHERE `id = currentFileId`. `relative_path` остаётся (путь не меняется) |
| `catalog_items` | UPDATE `name`, `normalized_name`, `updated_at_utc`, `content_hash`, `current_version_label` остаётся |
| `family_types` | DELETE+INSERT через `SyncTypesAsync` (collapse to current version, ADR-036) |

Подробная архитектура — см. [ADR-040](040-overwritecurrent-semantics.md).

## Consequences

- Пользователь не сможет случайно перезаписать или изменить managed-файл через проводник
- Revit может читать ReadOnly-файлы без ограничений (LoadFamily не требует записи)
- Код, работающий с managed storage, должен учитывать флаг при любых файловых операциях
- Встроен в `LocalFamilyImportService.ImportFileAsync()` при начальном импорте
- `OverwriteCurrent` — единственное исключение, при котором managed-файл текущей версии перезаписывается (см. ADR-040)
