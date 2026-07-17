using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
    [ObservableProperty]
    private ObservableCollection<FamilyVersionRow> _versions = [];

    [ObservableProperty]
    private FamilyVersionRow? _selectedVersionRow;

    [ObservableProperty]
    private bool _hasVersions;

    [ObservableProperty]
    private string? _versionsStatusMessage;

    /// <summary>
    /// Load all versions of the catalog item for display in the Versions tab.
    /// The active version (matching <c>catalog_items.current_version_label</c>)
    /// is marked with <see cref="FamilyVersionRow.IsActive"/> = true.
    /// Also attaches the type-name list to each row so:
    /// <list type="bullet">
    /// <item>the Types column shows the real count
    /// (<see cref="FamilyVersionRow.TypesCountDisplay"/> — not the sometimes
    /// NULL <c>catalog_versions.types_count</c>)</item>
    /// <item>the cell tooltip + row-details section show the names of types
    /// in that version (ADR-041 rev #5 UC-2)</item>
    /// </list>
    /// </summary>
    private async Task LoadVersionsAsync(CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FMProperties",
            ("Method", nameof(LoadVersionsAsync)),
            ("CatalogItemId", _catalogItemId));
        try
        {
            var versions = await _catalogProvider.GetVersionsAsync(_catalogItemId, ct).ConfigureAwait(true);
            var item = await _catalogProvider.GetItemAsync(_catalogItemId, ct).ConfigureAwait(true);
            var currentLabel = item?.CurrentVersionLabel;

            var rows = versions
                .Select(v => new FamilyVersionRow(
                    versionId: v.Id,
                    versionLabel: v.VersionLabel,
                    revitMajorVersion: v.RevitMajorVersion,
                    typesCount: v.TypesCount,
                    parametersCount: v.ParametersCount,
                    publishedAtUtc: v.PublishedAtUtc,
                    publishedAtText: v.PublishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    isActive: string.Equals(v.VersionLabel, currentLabel, System.StringComparison.Ordinal),
                    contentHash: v.ContentHash,
                    hashFormatVersion: v.HashFormatVersion,
                    publishedBy: v.PublishedBy,
                    typeNames: Array.Empty<string>(),
                    fileName: v.FileName))
                .ToList();

            // ADR-041 rev #5: attach type-name list per version for the
            // Types-column tooltip + row-details section (UC-2 — user can see
            // which types a version has without activating it). One roundtrip
            // per version — typical items have 3-5 versions, so the cost is
            // bounded (~10-30ms).
            foreach (var row in rows)
            {
                try
                {
                    var types = await _typeRepository.GetTypesForItemVersionAsync(
                        _catalogItemId, row.VersionId, ct).ConfigureAwait(true);
                    row.SetTypeNames(types.Select(t => t.Name).ToList());
                }
                catch (Exception typeEx)
                {
                    SmartConLogger.Warn(
                        $"LoadVersionsAsync: failed to load TypeNames for {row.VersionLabel}: {typeEx.Message} [Action: tooltip для колонки «Типы» будет пустым; остальные функции не затронуты]");
                }
            }

            Versions = new ObservableCollection<FamilyVersionRow>(rows);
            HasVersions = rows.Count > 0;
            SelectedVersionRow = rows.FirstOrDefault(r => r.IsActive) ?? rows.FirstOrDefault();
            VersionsStatusMessage = null;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"LoadVersionsAsync failed: {ex.Message} [Action: закройте и откройте свойства снова; проверьте подключение к БД]");
            VersionsStatusMessage = "Не удалось загрузить список версий";
            HasVersions = false;
            Versions = [];
        }
    }

    private bool CanMakeActive() => CanWrite()
        && SelectedVersionRow is not null
        && !SelectedVersionRow.IsActive;

    private bool CanDeleteVersion() => CanWrite()
        && SelectedVersionRow is not null
        && !SelectedVersionRow.IsActive;

    [RelayCommand(CanExecute = nameof(CanMakeActive))]
    private async Task MakeActiveAsync(CancellationToken ct)
    {
        if (SelectedVersionRow is null) return;

        using var _scope = SmartConLogger.BeginScope("FMProperties",
            ("Method", nameof(MakeActiveAsync)),
            ("CatalogItemId", _catalogItemId),
            ("VersionLabel", SelectedVersionRow.VersionLabel));

        var newLabel = SelectedVersionRow.VersionLabel;
        var oldLabel = Versions.FirstOrDefault(r => r.IsActive)?.VersionLabel;

        // Two-step UX (ADR-041 UC-3): preview → confirmation modal.
        // rev #5: the "preview" part of UC-2 is done entirely inside the
        // Versions tab via a compact tooltip on the Types cell — shows which
        // types the selected version has, without touching the Attributes tab.
        // The confirmation modal below is the final action step. After
        // confirmation, SetActiveVersionAsync switches current_version_label
        // and LoadAttributesDataAsync reloads the Attributes tab so it
        // reflects the new active version.
        var title = LanguageManager.GetString(StringLocalization.Keys.FM_Confirm_MakeActive_Title) ?? "Смена активной версии";
        var bodyTemplate = LanguageManager.GetString(StringLocalization.Keys.FM_Confirm_MakeActive_Body)
            ?? "Сделать версию {0} активной вместо {1}?";
        var body = string.Format(bodyTemplate, newLabel, oldLabel ?? "—");

        if (!_dialogService.ShowConfirmation(title, body))
        {
            SmartConLogger.Info("user cancelled MakeActive confirmation");
            return;
        }

        try
        {
            var result = await _writableProvider.SetActiveVersionAsync(
                _catalogItemId, newLabel, ct).ConfigureAwait(true);

            if (result.Success)
            {
                // Refresh internal state: flip IsActive flags.
                foreach (var row in Versions)
                {
                    row.IsActive = string.Equals(row.VersionLabel, newLabel, System.StringComparison.Ordinal);
                }
                SelectedVersionRow = Versions.FirstOrDefault(r => r.IsActive);
                VersionLabel = newLabel;
                // Issue #126: the item name follows the ACTIVE version's
                // file name. When the activated version was stored under a
                // different name, SetActiveVersionAsync has already renamed
                // the item in the DB — reflect it in the dialog.
                if (result.NameChanged && result.NewName is not null)
                {
                    Name = result.NewName;
                }
                SmartConLogger.Info(
                    $"MakeActive succeeded: prev={result.PreviousVersionLabel ?? "<null>"} " +
                    $"new={newLabel} hashSynced={result.ContentHashSynced} " +
                    $"nameChanged={result.NameChanged}" +
                    (result.NameChanged ? $" ('{result.PreviousName}' -> '{result.NewName}')" : string.Empty));
                DeleteVersionCommand.NotifyCanExecuteChanged();
                MakeActiveCommand.NotifyCanExecuteChanged();

                // ADR-041 rev #2: types and attribute values are per-version
                // (V18 migration + version-scoped SyncTypesAsync). After
                // SetActiveVersionAsync switches current_version_label, the
                // previously-loaded AvailableTypes / AttributeRows / ImportRunInfo
                // still reflect the OLD active version. Reload the Attributes
                // tab so the combobox, the type attribute values and the
                // "Импорт … • Revit X • N типов" info reflect the new active
                // version.
                try
                {
                    await LoadAttributesDataAsync(ct).ConfigureAwait(true);
                }
                catch (Exception reloadEx)
                {
                    SmartConLogger.Warn(
                        $"MakeActive succeeded but Attributes reload failed: {reloadEx.Message} [Action: закройте и откройте окно свойств, чтобы перечитать вкладку «Атрибуты» для новой активной версии]");
                }

                // ADR-042: reload assets (Model3D GLB + Images + Documents
                // + ...) for the new active version. Before this call the 3D
                // preview tab would show the OLD version's GLB until the
                // window was closed and reopened. GLB is stored per-version
                // (family_assets.version_label), so simply re-querying is
                // sufficient — no re-extraction is needed for rollback.
                try
                {
                    await LoadAssetsAsync(ct).ConfigureAwait(true);
                }
                catch (Exception reloadEx)
                {
                    SmartConLogger.Warn(
                        $"MakeActive succeeded but Assets reload failed: {reloadEx.Message} [Action: закройте и откройте окно свойств, чтобы перечитать вкладки «Содержимое» и «3D Просмотр» для новой активной версии]");
                }
            }
            else
            {
                // MakeActive failed: surface the backend error directly (don't
                // reuse FM_Error_CannotDeleteActive which is about deletion).
                _dialogService.ShowError("Family Manager", result.ErrorMessage ?? "Active version switch failed");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"MakeActiveAsync failed: {ex.Message} [Action: проверьте лог smartcon.log; повторите операцию; если не поможет — закройте и откройте окно свойств]");
            _dialogService.ShowError("Family Manager", ex.Message);
        }
    }

    partial void OnSelectedVersionRowChanged(FamilyVersionRow? value)
    {
        // The source generator does not auto-invalidate CanExecute when a
        // dependent ObservableProperty changes. Without this, switching
        // selection (active → non-active, or vice versa) leaves the
        // MakeActiveCommand/DeleteCommand in the previous enabled state
        // until the next manual NotifyCanExecuteChanged.
        //
        // ADR-041 rev #5: no preview reload is triggered here. The Versions
        // tab uses a compact tooltip on the Types cell — shows which types
        // the selected version has, without touching the Attributes tab.
        // To switch the Attributes tab to a non-active version's data, the
        // user must click "Сделать активной".
        MakeActiveCommand.NotifyCanExecuteChanged();
        DeleteVersionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteVersion))]
    private async Task DeleteVersion(CancellationToken ct)
    {
        if (SelectedVersionRow is null) return;

        using var _scope = SmartConLogger.BeginScope("FMProperties",
            ("Method", nameof(DeleteVersion)),
            ("CatalogItemId", _catalogItemId),
            ("VersionLabel", SelectedVersionRow.VersionLabel));

        var victimLabel = SelectedVersionRow.VersionLabel;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_Confirm_DeleteVersion_Title) ?? "Удаление версии";
        var bodyTemplate = LanguageManager.GetString(StringLocalization.Keys.FM_Confirm_DeleteVersion_Body)
            ?? "Удалить версию {0} семейства «{1}»? Это действие необратимо. Версия будет полностью удалена из каталога вместе с файлами, типами и атрибутами.";
        var body = string.Format(bodyTemplate, victimLabel, Name);

        if (!_dialogService.ShowConfirmation(title, body))
        {
            SmartConLogger.Info("user cancelled DeleteVersion confirmation");
            return;
        }

        try
        {
            var result = await _writableProvider.DeleteVersionAsync(
                _catalogItemId, victimLabel, ct).ConfigureAwait(true);

            if (result.Success)
            {
                // Remove the row(s) for the deleted label from the observable list.
                var toRemove = Versions.Where(r => r.VersionLabel == victimLabel).ToList();
                foreach (var row in toRemove)
                {
                    Versions.Remove(row);
                }
                HasVersions = Versions.Count > 0;
                SelectedVersionRow = Versions.FirstOrDefault(r => r.IsActive) ?? Versions.FirstOrDefault();
                SmartConLogger.Info(
                    $"DeleteVersion succeeded: rows={result.VersionsDeleted} " +
                    $"assets={result.AssetsDeleted} filesDeleted={result.FilesDeleted}");

                if (!result.FilesDeleted && result.PhysicalDirectoryPath is not null)
                {
                    var warnTitle = LanguageManager.GetString(StringLocalization.Keys.FM_Warn_FilesNotDeleted_Title) ?? "Файлы не удалены";
                    var warnBody = LanguageManager.GetString(StringLocalization.Keys.FM_Warn_FilesNotDeleted_Body)
                        ?? "Версия удалена из БД, но файлы на диске остались (вероятно, заблокированы Revit). Путь: {0}";
                    _dialogService.ShowWarning(warnTitle, string.Format(warnBody, result.PhysicalDirectoryPath));
                }
            }
            else
            {
                _dialogService.ShowError("Family Manager", result.ErrorMessage ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"DeleteVersionAsync failed: {ex.Message} [Action: проверьте лог smartcon.log; повторите попытку удаления; если файлы заблокированы — закройте соответствующий .rfa в Revit]");
            _dialogService.ShowError("Family Manager", ex.Message);
        }
    }
}
