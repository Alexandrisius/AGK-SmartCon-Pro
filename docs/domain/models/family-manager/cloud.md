---
module: family-manager
---

# Модели FamilyManager — Облачный каталог

> Загружать: при работе с cloud-каталогом FamilyManager (срез v1, ADR-075/076/077): publish/подписка, CloudLink на подключении базы, read-only гейт подписных копий.
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/CloudLink.cs`, `src/SmartCon.FamilyManager/Services/Cloud/*` (реализации — вне Core).

## CloudLink

Связь локальной базы с серверным каталогом облака (мастер-план §7.3.1): хранится отдельным свойством `DatabaseConnection.CloudLink` (ортогонально `Kind`/`ProjectBinding`) в `registry.json` (schemaVersion 2); дубль — в `catalog.db.database_meta.remote_source_json` (V39) для self-heal при даунгрейде плагина (E27). `Role=Subscribed` запрещает запись на уровне `IDbAccessControlService` (ADR-075 §7) — единственный writer подписной копии это sync.

**Файл:** `Models/FamilyManager/CloudLink.cs`

```csharp
public sealed record CloudLink(
    CloudLinkRole Role,
    string Endpoint,
    string CatalogId,
    string Slug,
    long? LastSyncedPublishSeq = null);
```

- `Endpoint` — базовый URL сервера; входит в target Credential Manager (изоляция аккаунтов разных серверов, ADR-076 §2).
- `Slug` — URL-friendly идентификатор каталога (транслитерация имени на сервере, уникализация `-2/-3`); в отличие от него отображаемое имя каталога человекочитаемое.
- `LastSyncedPublishSeq` — seq последней успешной публикации/pull; обновляется после publish/sync.

### CloudLinkRole

Роль связи: `Published` — база публикуется на сервер (push только явной командой «Опубликовать изменения», §7.3.12), `Subscribed` — локальная read-only копия подписки (обновление — кнопка обновления панели, §7.3.3; всегда только pull).

**Файл:** `Models/FamilyManager/CloudLink.cs`

```csharp
public enum CloudLinkRole
{
    Published = 0,
    Subscribed = 1,
}
```

---

## Сопутствующие контракты (члены существующих интерфейсов)

Cloud-срез не добавлял новых интерфейсов в Core — расширенные члены существующих контрактов:

- `IDatabaseManager.SetCloudLinkAsync(connectionId, link, ct)` — attach/replace/clear связи: registry + `database_meta.remote_source_json` (для Published; подписной копии базу пишет аплаер манифеста).
- `IDbAccessControlService.IsCloudReadOnly` — активная база является Subscribed-копией: AND-ится во все write-решения; auto-register `db_users` отключён (синтетический Engineer).
- `IFamilyGeometryPipeline.ExtractToPreviewCacheAsync(...)` — извлечение GLB в общий кэш `cloud-cache\previews\` ВНЕ копии (подписная копия read-only, ADR-075 §7).
- `IFamilyManagerDialogService.ShowCloudLogin/ShowCloudDatabaseWizard` (модальные), `ShowCloudOperationProgressDialog` (modeless, ADR-048).

Реализации (FamilyManager-слой, не Core): `CloudAuthService` (JWT + refresh-ротация в Windows Credential Manager через P/Invoke), `CloudCatalogApiClient` (REST, retry-once на 401), `CloudPublishService` (pull-before-push), `CloudSyncService` (идемпотентный pull с CAS-кэшем и атомарным swap), `CatalogManifestBuilder/Applier` (манифест v1, round-trip ≡), `CloudInvite` (строка приглашения `smartcon-cloud:subscribe:{base64url}`), `CloudDatabaseGate` (снапшот CloudLink активной базы для гейта).
