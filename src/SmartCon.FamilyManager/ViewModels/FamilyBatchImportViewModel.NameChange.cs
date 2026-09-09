using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyBatchImportViewModel
{
    /// <summary>
    /// v2.0.0 hotfix: re-resolve catalog status when the user renames a
    /// row. Debounced by <see cref="NameChangeDebounceMs"/> so we don't
    /// fire one DB query per keystroke. The lookup uses
    /// <see cref="FamilyNameNormalizer"/> to match the same canonical
    /// form the pre-build flow uses, so the row's Status flips
    /// New ↔ Existing as soon as the user types a name that no longer
    /// (or now) matches an existing catalog item.
    /// <para>
    /// v2.0.0: when an <see cref="IFamilyImportPrecomputer"/> is wired
    /// in, we also re-derive the precomputed
    /// (CatalogItemId, VersionLabel, ManagedPath) triple — without this
    /// re-derivation, the dialog would carry a stale precomputed id
    /// (the one from the row's original name) into the post-dialog
    /// import, and <c>ImportFileAsync</c> would try to
    /// <c>INSERT</c> a new <c>catalog_items</c> row with that id,
    /// tripping the <c>UNIQUE constraint failed: catalog_items.id</c>
    /// failure mode observed in the v2.0.0 manual run (the
    /// "Трубы → Трубы новые" rename in the active-project flow).
    /// </para>
    /// </summary>
    private void OnRowNameChanged(FamilyBatchImportRow row, string newName)
    {
        if (_batchApplying) return;
        if (_catalogProvider is null && _importPrecomputer is null) return;
        if (string.IsNullOrWhiteSpace(newName)) return;

        lock (_pendingNameChangesLock)
        {
            if (_pendingNameChanges.TryGetValue(row, out var previous))
            {
                previous.Cts.Cancel();
                previous.Cts.Dispose();
                _pendingNameChanges.Remove(row);
            }
        }

        var cts = new CancellationTokenSource();
        var token = cts.Token;

        var extension = ResolveExtensionForRow(row);

        var rowContentHash = row.PrecomputedContentHash;
        var rowHashFormatVersion = row.HashFormatVersion;
        var rowFamilySource = row.FamilySource;
        // Issue #192: system-row identity is the BuiltInCategory ordinal —
        // the dedup needs it for the category-based fallback lookup.
        var rowRevitCategoryId = row.Source is SmartCon.Core.Models.FamilyManager.FamilyImportSource.SystemSource sysSource
            ? sysSource.CategoryId
            : (int?)null;

        var renameTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(NameChangeDebounceMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                using var _scope = SmartConLogger.BeginScope("FMImport",
                    ("Method", nameof(OnRowNameChanged)),
                    ("Row", newName));

                var normalized = FamilyNameNormalizer.Normalize(newName);

                FamilyContentHash? contentHash = null;
                if (rowContentHash is not null && rowHashFormatVersion is not null)
                {
                    contentHash = new FamilyContentHash(
                        rowContentHash, rowHashFormatVersion.Value, rowFamilySource);
                }

                ContentHashDedupResult? dedupResult = null;
                if (_dedupService is not null)
                {
                    dedupResult = await _dedupService
                        .CheckAsync(normalized, contentHash, rowFamilySource, rowRevitCategoryId, token)
                        .ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                }

                var existing = _catalogProvider is null
                    ? null
                    : await _catalogProvider
                        .FindByNormalizedNameAsync(normalized, rowFamilySource, token)
                        .ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                var newStatus = dedupResult?.Status
                    ?? (existing is null
                        ? FamilyBatchImportStatus.New
                        : FamilyBatchImportStatus.Existing);
                var newExistingId = dedupResult?.ExistingCatalogItemId ?? existing?.Id;
                var newExistingVersionLabel = dedupResult?.ExistingVersionLabel ?? existing?.CurrentVersionLabel;
                var newMatchedVersionLabel = dedupResult?.HashMatch?.MatchedVersionLabel;
                var newIsCrossNameDuplicate = dedupResult?.IsCrossNameDuplicate ?? false;
                var newMatchedItemName = dedupResult?.HashMatch?.MatchedItemName;
                var isHashMatch = dedupResult?.HashMatch is not null;
                var newExistingCategoryId = existing?.CategoryId;
                var newExistingCategoryPath = existing?.CategoryPath;

                // Issue #135 defect 2: when dedup matched an item by
                // content hash (ADR-049, possibly under a different name),
                // the category must come from the HASH-MATCHED item — the
                // name lookup above misses for a unique new name and would
                // reset the category to «Без категории» even though the
                // duplicate's category should be preserved.
                if (isHashMatch && dedupResult!.ExistingCatalogItemId is not null && _catalogProvider is not null)
                {
                    try
                    {
                        var hashMatchedItem = await _catalogProvider
                            .GetItemAsync(dedupResult.ExistingCatalogItemId, token)
                            .ConfigureAwait(false);
                        if (hashMatchedItem is not null)
                        {
                            newExistingCategoryId = hashMatchedItem.CategoryId;
                            newExistingCategoryPath = hashMatchedItem.CategoryPath;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        SmartConLogger.Warn(
                            $"BatchImport.NameChange hash-match category lookup failed for '{dedupResult.ExistingCatalogItemId}': {ex.Message} " +
                            $"[Action: проверьте, что БД каталога доступна; категория строки может быть неактуальной]");
                    }
                    if (token.IsCancellationRequested) return;
                }

                // Issue #135 (P1): provenance the rename handler will
                // assign when the row is NOT locked. Locked rows keep
                // their Command/Manual provenance untouched.
                var newAutoProvenance = newStatus switch
                {
                    FamilyBatchImportStatus.Duplicate when isHashMatch => CategoryProvenance.AutoHash,
                    FamilyBatchImportStatus.Existing or FamilyBatchImportStatus.Duplicate => CategoryProvenance.AutoName,
                    _ => CategoryProvenance.None,
                };

                PrecomputedImportTriple? precomputed = null;
                if (_importPrecomputer is not null)
                {
                    // Issue #126: when dedup matched an item by content
                    // hash (possibly under a different name), the triple
                    // must target that item, not the name lookup.
                    precomputed = await _importPrecomputer
                        .BuildPrecomputedTripleAsync(newName, extension, rowFamilySource, dedupResult?.ExistingCatalogItemId, token)
                        .ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                }

                _dispatcher.Invoke(() => ApplyNameChangeResult(row, newStatus, newExistingId, newExistingVersionLabel, newExistingCategoryId, newExistingCategoryPath, precomputed, newMatchedVersionLabel, newIsCrossNameDuplicate, newMatchedItemName, newAutoProvenance));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"BatchImport.NameChange lookup failed: {ex.Message} [Action: проверьте, что БД каталога доступна; статус строки может быть неактуальным до Refresh]");
            }
        }, token);

        lock (_pendingNameChangesLock)
        {
            _pendingNameChanges[row] = (cts, renameTask);
        }
        _ = renameTask.ContinueWith(
            _ =>
            {
                lock (_pendingNameChangesLock)
                {
                    if (_pendingNameChanges.TryGetValue(row, out var current)
                        && ReferenceEquals(current.Task, renameTask))
                    {
                        _pendingNameChanges.Remove(row);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string ResolveExtensionForRow(FamilyBatchImportRow row)
    {
        // v2.0.0: extension is the file-type component of the
        // precomputed managed path. System-family rows stage .rvt
        // snapshots, loadable rows stage .rfa. We read it from the row
        // (not the catalog) because the row already carries the
        // resolved FamilySource — see FamilyBatchImportRow constructor.
        return row.FamilySource switch
        {
            "system" => ".rvt",
            _ => ".rfa"
        };
    }

    private void ApplyNameChangeResult(
        FamilyBatchImportRow row,
        FamilyBatchImportStatus newStatus,
        string? newExistingId,
        string? newExistingVersionLabel,
        string? newExistingCategoryId,
        string? newExistingCategoryPath,
        PrecomputedImportTriple? precomputed,
        string? matchedVersionLabel = null,
        bool isCrossNameDuplicate = false,
        string? matchedItemName = null,
        CategoryProvenance newAutoProvenance = CategoryProvenance.None)
    {
        if (row.Status != newStatus)
        {
            row.Status = newStatus;
        }
        row.ExistingCatalogItemId = newExistingId;
        row.ExistingVersionLabel = newExistingVersionLabel;
        row.MatchedVersionLabel = matchedVersionLabel;
        // #180: a rename re-dedup resolves the version by content hash only
        // (the ES marker is not consulted at the dialog level) — the shown
        // label is hash-originated again, so the marker-origin annotation
        // must not survive.
        row.IsMarkerResolvedVersion = false;
        row.IsCrossNameDuplicate = isCrossNameDuplicate;
        row.MatchedItemName = matchedItemName;

        // Issue #135 (P2): the existing item's REAL category always
        // follows the lookup — even for locked rows — so the move-warning
        // can compare it against the locked target category.
        row.ExistingCategoryId = newExistingCategoryId;
        row.ExistingCategoryPath = newExistingCategoryPath;

        if (!row.TargetCategoryIsManual)
        {
            var previousCategoryId = row.TargetCategoryId;
            if ((newStatus == FamilyBatchImportStatus.Existing || newStatus == FamilyBatchImportStatus.Duplicate) && newExistingId is not null)
            {
                // Provenance BEFORE the path: the CategoryChanged
                // batch-apply (fired by the path setter) must observe the
                // row's new provenance to decide about locked targets.
                row.CategoryProvenance = newAutoProvenance;
                row.TargetCategoryId = newExistingCategoryId;
                row.TargetCategoryPath = !string.IsNullOrWhiteSpace(newExistingCategoryPath)
                    ? newExistingCategoryPath!
                    : (LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "Без категории");
            }
            else
            {
                row.CategoryProvenance = CategoryProvenance.None;
                row.TargetCategoryId = null;
                row.TargetCategoryPath = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "Без категории";
            }

            // Issue #135 (P4): make the silent auto-change visible — the
            // view flashes the category cell on every token increment.
            if (!string.Equals(previousCategoryId, row.TargetCategoryId, StringComparison.Ordinal))
            {
                row.CategoryFlashToken++;
                SmartConLogger.Debug(
                    $"BatchImport.Category: '{row.FileName}' auto-category " +
                    $"'{previousCategoryId ?? "<none>"}' -> '{row.TargetCategoryId ?? "<none>"}' " +
                    $"(status={newStatus}, provenance={row.CategoryProvenance})");
            }

            // #241: re-evaluate the assignment rules with the new name —
            // an automatic-provenance row may get its category back (or
            // lose it, or become ambiguous). A matched category fires the
            // row's CategoryChanged chain (gate revalidation included).
            // Runs on the UI thread: ApplyNameChangeResult is invoked via
            // _dispatcher.
            TryAutoAssignRow(row);
        }
        else
        {
            SmartConLogger.Debug(
                $"BatchImport.Category: '{row.FileName}' rename kept locked category " +
                $"'{row.TargetCategoryId ?? "<none>"}' (status={newStatus}, provenance={row.CategoryProvenance})");

            // #241: a locked row keeps its category, but the RULES'
            // recommendation still follows the new name — the category
            // column's warning icon must reflect the renamed family.
            TryAutoAssignRow(row);
        }

        if (row.ShowCategoryMoveWarning)
        {
            SmartConLogger.Debug(
                $"BatchImport.Category: '{row.FileName}' will MOVE existing item from " +
                $"'{row.ExistingCategoryPath ?? row.ExistingCategoryId}' to '{row.TargetCategoryPath}' on import");
        }

        if (precomputed is not null)
        {
            row.PrecomputedCatalogItemId = precomputed.CatalogItemId;
            row.PrecomputedVersionLabel = precomputed.VersionLabel;
            row.PrecomputedManagedPath = precomputed.ManagedPath;
        }
        else
        {
            row.PrecomputedCatalogItemId = null;
            row.PrecomputedVersionLabel = null;
            row.PrecomputedManagedPath = null;
        }

        // E2 (#209): a rename may flip a dependency row into/out of the
        // outdated-nested state — recompute the parents' import blocks.
        if (row.DependencyLinks is not null)
        {
            RefreshDependencyIndicators();
        }
    }
}
