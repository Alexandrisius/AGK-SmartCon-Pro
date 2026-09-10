using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// E2 (#209, ADR-066): after a parent family is loaded into the project,
/// every dependency child recorded on the parent's current version receives
/// the ES version marker of the EMBEDDED version
/// (<c>family_dependencies.child_version_label</c>) — the nested copies the
/// load planted into the project ARE that embedded content. The children
/// thereby join the stale cycle: when a newer child version is activated
/// later, the project copy reads as stale and the user refreshes it with
/// the regular «Обновить» flow. Links with unknown embedded version (NULL)
/// are skipped — a guessed marker would be worse than none. Failures never
/// fail the load: an unmarked nested family is simply invisible to the
/// stale check until the next explicit load.
/// </summary>
internal static class NestedDependencyMarkerWriter
{
    public static async Task<int> WriteMarkersAsync(
        IFamilyDependencyRepository dependencyRepository,
        IFamilyCatalogProvider catalogProvider,
        IFamilyVersionWriter versionWriter,
        string parentCatalogItemId,
        int targetRevitVersion,
        CancellationToken ct)
    {
        var links = await dependencyRepository
            .GetForCurrentVersionAsync(parentCatalogItemId, ct)
            .ConfigureAwait(false);
        if (links.Count == 0)
        {
            return 0;
        }

        var written = 0;
        foreach (var link in links)
        {
            if (string.IsNullOrEmpty(link.ChildVersionLabel))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            try
            {
                var childItem = await catalogProvider
                    .GetItemAsync(link.ChildCatalogItemId, ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(childItem?.Name))
                {
                    continue;
                }

                await versionWriter.WriteVersionMarkerAsync(
                    link.ChildCatalogItemId,
                    childItem!.Name,
                    link.ChildVersionLabel!,
                    targetRevitVersion,
                    ct).ConfigureAwait(false);
                written++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Nested dependency marker write failed (child={link.ChildCatalogItemId}, " +
                    $"label={link.ChildVersionLabel}): {ex.GetType().Name}: {ex.Message}. " +
                    "[Action: вложенное семейство в проекте останется без маркера — Проверить покажет его stale только после явной загрузки]");
            }
        }

        if (written > 0)
        {
            SmartConLogger.Info(
                $"Nested dependency markers written: {written} (parent CatalogItemId={parentCatalogItemId})");
        }
        return written;
    }
}
