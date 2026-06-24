# ADR-035: Remove temp staging infrastructure (v2.0.0)

- **Status:** Accepted
- **Date:** 2026-06-24
- **Phase:** 27 — Family Manager temp removal
- **Supersedes:** ADR-024 (ActiveFamilyFilePreparer)
- **Relates to:** ADR-030 (v2.0.0 clean slate), ADR-033 (Type Catalog bake-in)

## Context

The Family Manager module historically created temporary staging files in
`%TEMP%\SmartCon\` to support the following behaviours:

1. **SHA-256 dedup** — compute the content hash of an `.rfa` to detect exact
   duplicates already imported into the catalog.
2. **File size display** — show the user the size of each candidate file in
   the batch dialog.
3. **"Duplicate" status** — mark rows in the batch dialog as `Duplicate`
   (forced-Skip) when SHA-256 matched an existing catalog entry.
4. **Type Catalog sidecar** — copy `.txt` next to the staged `.rfa` so the
   bake could resolve the sidecar by `Path.ChangeExtension`.

These behaviours were implemented via four cooperating services:

- `ActiveFamilyFilePreparer` — SaveAs of the active Revit family document
  into `%TEMP%\SmartCon\FMLoad\<guid>\`.
- `ActiveImportCleanupService` — recursive cleanup of `%TEMP%\SmartCon\FMLoad`
  and `%TEMP%\SmartCon\SystemFamilyLoadFromProject` after each import.
- `SystemFamilyTempLayout` — string constants for the temp paths.
- `SystemFamilyRevitOperations.CreateCleanProjectWithTypesAndInstances` —
  SaveAs of an isolated mini-project into `%TEMP%\...`.
- `Sha256FileHasher` — full-file SHA-256 streaming hasher.

Three problems motivated removal:

1. **Disk churn** — every batch import of an active `.rvt` document wrote
   the file twice (active → temp → managed). For large libraries this
   doubled I/O and disk usage for no user-visible benefit.
2. **Cancel bug** — when the user cancelled the batch dialog, the temp
   staging file forced `CloseFamilyDocumentAsync` to close the user's
   currently-open family document, discarding unsaved edits.
3. **Stale temp folders** — Revit crashes mid-import left orphaned temp
   files that needed periodic cleanup, otherwise the disk filled.

## Decision

Remove the entire temp staging layer. The catalog no longer tracks SHA-256
or file size, and the active-document import no longer stages through temp.

### Removed

- `ActiveFamilyFilePreparer` and `IActiveFamilyFilePreparer`
- `ActiveImportCleanupService` and `IActiveImportCleanupService`
- `SystemFamilyTempLayout`
- `ActiveFamilyPreparationResult` (DTO)
- `LocalFamilySidecarLocator` and `IFamilySidecarLocator`
- `Sha256FileHasher`
- `FileNameOnlyMetadataExtractionService` (replaced with `FileMetadataExtractionService`)
- `FileSizeToMbConverter`, `NotEqualConverter` (UI converters only used for these fields)
- `FamilyBatchImportStatus.Duplicate` (no longer detectable)
- `FamilyBatchImportItem.Sha256` and `FileSizeBytes` fields
- `FamilyFileRecord.Sha256` and `SizeBytes` fields
- `FamilyCatalogVersion.Sha256` field
- `FamilyMetadataExtractionResult.Sha256` and `FileSizeBytes` fields
- `FamilyDataImportRun.SourceSha256` field
- `FamilyBatchImportItem.OriginalSourcePath` (no longer needed without sidecar lookup)

### Replaced

- `FileNameOnlyMetadataExtractionService` → `FileMetadataExtractionService`
  (no SHA-256 dependency, no `Sha256FileHasher` constructor argument).
- `LocalCatalogProvider.FindByHashAsync` removed; only
  `FindByNormalizedNameAsync` remains.
- `LocalFamilyImportService` no longer performs SHA-256 dedup. Same-name
  files produce a new version (`vN+1`); the user can pick `OverwriteCurrent`
  in the batch dialog to replace the current version instead.

### Schema migration v14

```sql
BEGIN IMMEDIATE;
DROP INDEX IF EXISTS ix_family_files_sha256;
DROP INDEX IF EXISTS ix_catalog_versions_sha256;
ALTER TABLE family_files DROP COLUMN size_bytes;
ALTER TABLE family_files DROP COLUMN sha256;
ALTER TABLE catalog_versions DROP COLUMN sha256;
ALTER TABLE family_data_import_runs DROP COLUMN source_sha256;
UPDATE schema_info SET value = '14' WHERE key = 'schema_version';
COMMIT;
```

Drop is wrapped in a single `BEGIN IMMEDIATE` transaction so a partial
failure rolls back the entire migration. SQLite 3.35+ (used by
`Microsoft.Data.Sqlite 8.x`) supports `ALTER TABLE DROP COLUMN`.

### UC-2 (Import Active Family) flow change

Before v2.0.0:

```
Active family document in Revit
   └─→ ActiveFamilyFilePreparer.SaveAs → %TEMP%\SmartCon\FMLoad\{guid}\file.rfa
       └─→ Batch dialog (with SHA-256 dedup status)
           └─→ Cancel → CloseFamilyDocumentAsync (closes active doc!)
           └─→ Confirm → ImportFileAsync(tempRfaPath)
                          └─→ ActiveImportCleanupService cleans up temp
```

After v2.0.0:

```
Active family document in Revit
   └─→ Batch dialog (placeholder FilePath = "active://{Name}")
       └─→ Cancel → no temp file was created, active doc stays open
       └─→ Confirm → compute managed path → SaveAs into managed storage
                   → OpenDocumentFile(managed) for extraction
                   → active doc stays open with PathName = managed path
```

`CloseFamilyDocumentAsync` is **retained** for the post-import teardown
of the orphan family document (the one Revit opened when the user invoked
"Редактировать" on a managed catalog item — see issue #79). The hotfix
path keeps the active document open across a cancelled batch dialog
(returns `false` from `ProcessFamilyImportAsync`, no `CloseFamilyDocumentAsync`
call), and only invokes `CloseFamilyDocumentAsync(managedRfaPath)` when
the user confirmed the import — at which point the now-orphaned family
document is closed and focus returns to the host project.

### UC-3 / UC-4 (Import Active Project / Selected Elements) flow change

Before v2.0.0: temp files for system categories and loadable families were
created in `%TEMP%\SmartCon\SystemFamilyLoadFromProject\{guid}\` before the
batch dialog appeared, leaving orphan files if the user cancelled. Loadable
families used a separate `dbRoot/files/_stage/{guid}/{name}.rfa` transient
location, also leaking on cancel.

After v2.0.0:
- `ComputeSystemFamilyManagedPath(displayName)` and
  `ComputeLoadableFamilyManagedPath(familyName)` allocate the canonical
  managed path (`{db-root}/files/{newGuid}/v1/{name}.r{fa|vt}`) up front.
- The isolation project service (`CreateCleanProjectWithTypesAndInstances`)
  and the loadable staging helper (`StageLoadableFamilyFromProject`) both
  `SaveAs` directly into managed storage — no `_stage/` fallback, no temp.
- `LocalFamilyImportService.PrepareManagedRfaAsync` gains an early-out
  that skips the copy when the source already lives under
  `{db-root}/files/`, removing the duplicate managed copy that UC-3/UC-4
  used to produce before `ImportBatchAsync` registered the new catalog item.
- `App.OnStartup` runs `CleanupLegacyStageFolder` once on upgrade to remove
  any `_stage/` folder left behind by prior versions. The path is idempotent
  (no folder → no-op).
- If the user cancels the batch dialog, the orphan `.rvt` / `.rfa` files
  remain in managed storage but are unreferenced from the catalog. A future
  Phase can add an orphan-sweep task (not in scope here).

### Type Catalog and read-only files

`%TEMP%` is never touched. Type Catalog `.txt` files are read directly from
the user's disk via `Path.ChangeExtension(sourceFilePath, ".txt")` (ADR-033
bake-in). The `.txt` is never copied to managed storage; all types and
formula-driven values are baked into the managed `.rfa` itself by
`RevitFamilyTypeCatalogBaker`.

A read-only flag on the source `.rfa` (e.g. a managed file the user
re-opened via "Edit") does not block import — the baker opens the file in
an in-memory copy and uses `SaveAs` to write the managed copy.

## Consequences

### Positive

- One disk write per import instead of two.
- User-cancel of the batch dialog no longer closes the active family
  document (bug fix for the data-loss risk).
- No more temp-folder cleanup at startup or shutdown.
- Catalog schema is simpler — fewer redundant columns.
- Batch dialog is simpler — no "Duplicate" status, no SHA-256 dedup
  pass before showing the dialog.

### Negative / migration

- v2.0.0 is a **breaking change**. Users upgrading from 1.x must
  re-import their `.rfa` files into a fresh catalog database. The
  schema migration v14 drops columns but keeps the same file storage
  layout, so previously-imported families remain in managed storage but
  their SHA-256 / size values are gone.
- Same-name `.rfa` files with different content now create new versions
  instead of being flagged as duplicates. The user can pick
  `OverwriteCurrent` in the batch dialog to keep the catalog tidy.
- Orphan managed files after UC-3 / UC-4 cancel are not cleaned up
  automatically (manual `registry.json` cleanup needed for affected DBs).
- Test suite dropped from 1445 tests by 13 (the `Sha256FileHasher`,
  cleanup, dedup, and sidecar tests).

### Migration guide for users

> SmartCon 2.0 removes the deduplication cache and the temporary staging
> folder. To migrate:
>
> 1. Back up `%APPDATA%\SmartCon\FamilyManager\registry.json` (your DB list).
> 2. Delete the contents of `%APPDATA%\SmartCon\FamilyManager\databases\`
>    (the actual catalog DBs and managed `.rfa` files).
> 3. Start Revit with SmartCon 2.0 — it will create a fresh v14 schema.
> 4. Re-import your families via "Импорт файлов" or "Импорт активного
>    проекта". Same-name files will appear as new versions.
>
> Old v13 databases will be auto-migrated to v14 (drops SHA-256 / size
> columns). However, we recommend the clean-slate path above since
> schema v14 has no real reason to keep v13's hash data.

## Alternatives considered

1. **Keep SHA-256 dedup, compute hash on the fly without temp staging.**
   Rejected: the dedup outcome was never critical for users (most
   import paths already pass the same file once), and it doubles the
   I/O on every import of an active family document.

2. **Compute SHA-256 against the source file before SaveAs, store in
   memory only.** Rejected: still requires reading the entire file, and
   we'd have nowhere meaningful to display the result.

3. **Side-by-side: keep temp folders, but skip them on user cancel.**
   Rejected: extra code, extra disk writes, extra cleanup logic — defeats
   the purpose.

## References

- ADR-024 — superseded
- ADR-030 — v2.0.0 clean slate for stale detection
- ADR-033 — Type Catalog bake-in (the reason `.txt` no longer needs to be
  in managed storage)
- ADR-034 — shared nested families
