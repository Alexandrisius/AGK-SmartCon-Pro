---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Документы, файлы и ассеты

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IActiveDocumentClassifier

Определяет тип активного документа: `Family` / `Project` / `None`.
Используется командой «Импорт активного файла» для выбора code path.

**Файл:** `IActiveDocumentClassifier.cs`
**Реализация:** `SmartCon.FamilyManager/Services/ActiveDocumentClassifier.cs`

```csharp
public enum ActiveDocumentKind { None, Family, Project }

public interface IActiveDocumentClassifier
{
    Task<ActiveDocumentKind> ClassifyAsync(CancellationToken ct = default);
}
```

---

## IFamilyFileResolver

Разрешение путей к файлам семейств из managed storage. Выбирает лучший файл для целевой версии Revit.

**Файл:** `IFamilyFileResolver.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyFileResolver.cs`

```csharp
public interface IFamilyFileResolver
{
    Task<FamilyResolvedFile> ResolveForLoadAsync(string catalogItemId, int targetRevitVersion, CancellationToken ct = default);
    string? GetDatabaseRoot();
}
```

---

## IFamilyStorageRenameService

Переименование физических `.rfa` файлов в managed storage при изменении отображаемого имени семейства. Переименовывает только файлы **текущей версии** (`current_version_label`) во **всех подпапках Revit-версий** (`r24/`, `r25/`...). Исторические версии (`v1`, `v2`...) остаются нетронутыми. Обновляет `family_files.file_name` и `family_files.relative_path` в БД.

**Файл:** `IFamilyStorageRenameService.cs`  
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyStorageRenameService.cs`

```csharp
public interface IFamilyStorageRenameService
{
    Task RenameFamilyFilesAsync(string catalogItemId, string newName, CancellationToken ct = default);
}
```

---

## IFamilyAssetService

Управление вспомогательными ассетами (изображения, документы, lookup tables) семейств.
С ADR-047 (issue #131) также управляет производным файлом аватара `avatar.png` (560×420):
`SetPrimaryAssetAsync` инвалидирует его при смене primary, `GetAvatarImagePathAsync` —
единая цепочка разрешения (`avatar.png` → primary image) для аватарки и tooltip.

**Файл:** `IFamilyAssetService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyAssetService.cs`

```csharp
public interface IFamilyAssetService
{
    Task<FamilyAsset> AddAssetAsync(string catalogItemId, string? versionLabel, FamilyAssetType assetType, string sourceFilePath, string? description, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyAsset>> GetAssetsAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);
    Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct = default);
    Task<string?> ResolveAssetPathAsync(string assetId, CancellationToken ct = default);
    Task SetPrimaryAssetAsync(string assetId, CancellationToken ct = default);
    Task<FamilyAsset?> GetPrimaryImageAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);
    Task SetAssetVersionBindingAsync(string assetId, string? newVersionLabel, CancellationToken ct = default);
    Task SaveAvatarAsync(string catalogItemId, string sourcePngPath, CancellationToken ct = default);
    Task<string?> GetAvatarImagePathAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);
    Task ClearAvatarAsync(string catalogItemId, CancellationToken ct = default);
}
```

---

## IAvatarCropService

Рендер единой миниатюры аватара (560×420 PNG, ADR-047) из исходного изображения любого
разрешения. Декод ограничен 4096px по ширине — защита памяти для очень больших файлов.

**Файл:** `IAvatarCropService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/WpfAvatarCropService.cs` (WPF imaging)

```csharp
public interface IAvatarCropService
{
    (int PixelWidth, int PixelHeight) GetImageDimensions(string path);
    void CropToPng(string sourcePath, ImageCropRect sourceRect, string outputPath);
}
```

---

## IActiveDocumentChangeNotifier

Абстрация над Revit-событиями `ViewActivated`, `DocumentSaved` и `DocumentSavedAs`.
Core-контракт, реализация в `SmartCon.Revit/Events/ActiveDocumentChangeNotifier.cs`.

**Файл:** `IActiveDocumentChangeNotifier.cs`

```csharp
public interface IActiveDocumentChangeNotifier : IDisposable
{
    event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;
    event EventHandler<ActiveDocumentPathChangedEventArgs>? ActiveDocumentPathChanged;
}

public enum ActiveDocumentPathChangeReason
{
    Activated,
    Saved,
    SavedAs
}

public sealed record ActiveDocumentChangedEventArgs(string FilePath);
public sealed record ActiveDocumentPathChangedEventArgs(string FilePath, ActiveDocumentPathChangeReason Reason);
```

- `ActiveDocumentChanged` — сработал `ViewActivated` для несемейного, сохранённого документа (путь гарантированно не пустой).
- `ActiveDocumentPathChanged` — у активного документа изменился путь, пока он остаётся активным: первое сохранение нового проекта (`SavedAs`) или переименование через `SaveAs` (`SavedAs`), а также редкий случай сохранения без смены пути (`Saved`).

Подписчики (`FamilyManagerMainViewModel`) получают путь активного документа и
запускают `IProjectBaseActivator.ActivateForDocumentAsync`. Фильтрация unsaved/detached/family
документов, неактивных документов и отменённых/неудавшихся сохранений выполняется в реализации,
чтобы Core оставался чистым от Revit API. См. Issue #128.
