# FamilyManager Provider Contract

## Цель документа

Provider contract должен позволить MVP работать с локальным SQLite-каталогом и позже добавить серверные/корпоративные каталоги без переписывания UI.

## Provider Types

| Provider | MVP | Description |
| --- | --- | --- |
| `LocalCatalogProvider` | Да | SQLite + local file cache |
| `RemoteCatalogProvider` | Нет | HTTP API |
| `CorporateCatalogProvider` | Нет | Self-hosted organization storage |
| `PublicReadOnlyProvider` | Нет | Public/manufacturer catalog |
| `CompositeCatalogProvider` | Нет | Unified view across providers |

## Core Capabilities

Provider must expose capabilities instead of forcing all providers to support all operations.

| Capability | Meaning |
| --- | --- |
| `CanRead` | Provider can list/search items |
| `CanWriteMetadata` | Provider can update tags/status/description |
| `CanImportFiles` | Provider can ingest new `.rfa` files |
| `CanDownloadFiles` | Provider can resolve local file path or download |
| `CanDelete` | Provider supports delete/archive |
| `CanSync` | Provider supports sync |
| `IsOnlineRequired` | Provider requires network |
| `IsReadOnly` | Provider cannot be modified |

## Suggested Interfaces

### `IFamilyCatalogProvider`

```csharp
public interface IFamilyCatalogProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    CatalogProviderKind Kind { get; }
    FamilyCatalogCapabilities Capabilities { get; }

    Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(
        FamilyCatalogQuery query,
        CancellationToken cancellationToken);

    Task<FamilyCatalogItem?> GetItemAsync(
        Guid itemId,
        CancellationToken cancellationToken);

    Task<FamilyCatalogVersion?> GetVersionAsync(
        Guid versionId,
        CancellationToken cancellationToken);
}
```

### `IWritableFamilyCatalogProvider`

```csharp
public interface IWritableFamilyCatalogProvider : IFamilyCatalogProvider
{
    Task<FamilyCatalogItem> UpsertItemAsync(
        FamilyCatalogItem item,
        CancellationToken cancellationToken);

    Task ArchiveItemAsync(
        Guid itemId,
        CancellationToken cancellationToken);
}
```

### `IFamilyImportService`

```csharp
public interface IFamilyImportService
{
    Task<FamilyImportResult> ImportFileAsync(
        FamilyImportRequest request,
        CancellationToken cancellationToken);

    Task<FamilyBatchImportResult> ImportFolderAsync(
        FamilyFolderImportRequest request,
        IProgress<FamilyImportProgress> progress,
        CancellationToken cancellationToken);
}
```

### `IFamilyFileResolver`

```csharp
public interface IFamilyFileResolver
{
    Task<FamilyResolvedFile> ResolveForLoadAsync(
        Guid versionId,
        CancellationToken cancellationToken);
}
```

### `IFamilyLoadService`

Revit-facing interface implemented in `SmartCon.Revit`.

```csharp
public interface IFamilyLoadService
{
    FamilyLoadResult LoadFamily(
        object revitDocument,
        FamilyResolvedFile file,
        FamilyLoadOptions options);
}
```

`object revitDocument` здесь отражает opaque boundary. Точная сигнатура может использовать `Document` как carrier только если это соответствует текущему Core pattern и I-09.

## Query Model

| Field | Type | MVP |
| --- | --- | --- |
| `Text` | `string?` | Да |
| `Statuses` | `IReadOnlyList<FamilyContentStatus>` | Да |
| `Categories` | `IReadOnlyList<string>` | Да |
| `Tags` | `IReadOnlyList<string>` | Да |
| `Manufacturer` | `string?` | Да |
| `ProviderIds` | `IReadOnlyList<string>` | Да |
| `Skip` | `int` | Да |
| `Take` | `int` | Да |
| `Sort` | enum | Да |

## Provider Error Model

| Error | Meaning |
| --- | --- |
| `ProviderUnavailable` | Remote/offline provider недоступен |
| `FileMissing` | Файл невозможно разрешить |
| `PermissionDenied` | Нет прав |
| `CatalogCorrupted` | SQLite/payload повреждён |
| `UnsupportedOperation` | Capability отсутствует |
| `VersionConflict` | Будущий server conflict |

## MVP Implementation

MVP implements:

- `LocalCatalogProvider`;
- `LocalFamilyImportService`;
- `LocalFamilyFileResolver`;
- `RevitFamilyLoadService`;
- `ProjectFamilyUsageRepository`.

MVP does not implement:

- remote auth;
- sync;
- server conflict resolution;
- cloud download;
- composite provider.

## Threading Contract

| Operation | Threading |
| --- | --- |
| SQLite read/write | Background allowed with own connection |
| Hash calculation | Background allowed |
| File copy | Background allowed |
| Revit metadata extraction | Revit-safe path only |
| Load to project | Revit main thread / ExternalEvent |
