using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
    [ObservableProperty] private ContentCategory? _selectedContentCategory = ContentCategory.All;
    [ObservableProperty] private ObservableCollection<CategoryNavItem> _categoryItems = [];
    [ObservableProperty] private ObservableCollection<ContentAssetRow> _filteredAssets = [];
    [ObservableProperty] private bool _isAttachToVersionVisible;

    private List<ContentAssetRow> _allVisibleRows = [];
    private Dictionary<string, string?> _resolvedPaths = [];

    public bool HasFilteredAssets => FilteredAssets.Count > 0;

    partial void OnSelectedContentCategoryChanged(ContentCategory? value)
    {
        RebuildFilteredAssets();
    }

    partial void OnFilteredAssetsChanged(ObservableCollection<ContentAssetRow> value)
    {
        OnPropertyChanged(nameof(HasFilteredAssets));
    }

    /// <summary>
    /// Called from <see cref="OnVersionLabelChanged"/> in the main partial to
    /// update content-tab state when the version context changes.
    /// </summary>
    private void OnVersionLabelChangedForAssets(string? value)
    {
        IsAttachToVersionVisible = !string.IsNullOrEmpty(value);
    }

    private async Task WithBusyStateAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task LoadAssetsAsync(CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FMProperties",
            ("Method", "LoadAssetsAsync"),
            ("CatalogItemId", _catalogItemId));
        var assets = await _assetService.GetAssetsAsync(_catalogItemId, VersionLabel, ct);

        ImageAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Image));
        VideoAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Video));
        DocumentAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Document));
        LookupAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.LookupTable));
        SpreadsheetAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Spreadsheet));
        Model3DAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Model3D));
        OtherAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Other));

        Populate3DTypeNames();

        var primary = assets.FirstOrDefault(a => a.AssetType == FamilyAssetType.Image && a.IsPrimary);
        if (primary is null)
            primary = ImageAssets.FirstOrDefault();

        if (primary is not null)
        {
            var path = await _assetService.ResolveAssetPathAsync(primary.Id, ct);
            AvatarImagePath = path;
            HasAvatar = path is not null;
        }
        else
        {
            AvatarImagePath = null;
            HasAvatar = false;
        }

        await PreResolveAssetPathsAsync(assets, ct);
        RebuildContentTabData();
    }

    private async Task PreResolveAssetPathsAsync(IReadOnlyList<FamilyAsset> assets, CancellationToken ct)
    {
        _resolvedPaths = new Dictionary<string, string?>();
        foreach (var a in assets)
        {
            if (IsAutoExtractedPreview(a)) continue;
            if (a.AssetType == FamilyAssetType.Image)
            {
                var path = await _assetService.ResolveAssetPathAsync(a.Id, ct);
                _resolvedPaths[a.Id] = path;
            }
        }
    }

    private static bool IsAutoExtractedPreview(FamilyAsset a) =>
        !string.IsNullOrEmpty(a.Description) &&
        a.Description!.StartsWith("auto-extracted-preview:", StringComparison.Ordinal);

    /// <summary>
    /// Rebuilds <see cref="_allVisibleRows"/> (excluding auto-extracted GLB and
    /// user-added Model3D files which are handled elsewhere), <see cref="CategoryItems"/>
    /// sidebar with counts, and <see cref="FilteredAssets"/>.
    /// </summary>
    private void RebuildContentTabData()
    {
        var canToggle = IsAttachToVersionVisible;
        _allVisibleRows = new List<ContentAssetRow>();
        AddVisibleRows(ImageAssets);
        AddVisibleRows(VideoAssets);
        AddVisibleRows(DocumentAssets);
        AddVisibleRows(LookupAssets);
        AddVisibleRows(SpreadsheetAssets);
        AddVisibleRows(OtherAssets);

        var allCount = _allVisibleRows.Count;
        var imgCount = CountVisible(ImageAssets);
        var vidCount = CountVisible(VideoAssets);
        var docCount = CountVisible(DocumentAssets);
        var tableCount = CountVisible(LookupAssets) + CountVisible(SpreadsheetAssets);
        var othCount = CountVisible(OtherAssets);

        CategoryItems = new ObservableCollection<CategoryNavItem>
        {
            new(ContentCategory.All, null,
                LanguageManager.GetString(StringLocalization.Keys.FM_Props_CategoryAll) ?? "All",
                "AssetAllGeometry", allCount),
            new(ContentCategory.Image, FamilyAssetType.Image,
                LanguageManager.GetString(StringLocalization.Keys.FM_Props_Images) ?? "Images",
                "AssetImageGeometry", imgCount),
            new(ContentCategory.Video, FamilyAssetType.Video,
                LanguageManager.GetString(StringLocalization.Keys.FM_Props_Videos) ?? "Videos",
                "AssetVideoGeometry", vidCount),
            new(ContentCategory.Document, FamilyAssetType.Document,
                LanguageManager.GetString(StringLocalization.Keys.FM_Props_Documents) ?? "Documents",
                "AssetDocumentGeometry", docCount),
            new(ContentCategory.Table, null,
                LanguageManager.GetString(StringLocalization.Keys.FM_Props_Tables) ?? "Tables",
                "AssetLookupTableGeometry", tableCount),
            new(ContentCategory.Other, FamilyAssetType.Other,
                LanguageManager.GetString(StringLocalization.Keys.FM_Props_Other) ?? "Other",
                "AssetOtherGeometry", othCount),
        };

        RebuildFilteredAssets();

        void AddVisibleRows(ObservableCollection<FamilyAsset> source)
        {
            foreach (var a in source)
            {
                if (IsAutoExtractedPreview(a)) continue;
                _resolvedPaths.TryGetValue(a.Id, out var resolved);
                _allVisibleRows.Add(new ContentAssetRow(a, resolved, canToggle));
            }
        }

        static int CountVisible(ObservableCollection<FamilyAsset> source)
        {
            var n = 0;
            foreach (var a in source)
                if (!IsAutoExtractedPreview(a))
                    n++;
            return n;
        }
    }

    /// <summary>
    /// Rebuilds <see cref="FilteredAssets"/> from <see cref="_allVisibleRows"/>
    /// based on <see cref="SelectedContentCategory"/>. Files are sorted by
    /// <see cref="FamilyAsset.CreatedAtUtc"/> descending (newest first), with
    /// primary image always at the top.
    /// </summary>
    private void RebuildFilteredAssets()
    {
        IEnumerable<ContentAssetRow> source = _allVisibleRows;

        source = SelectedContentCategory switch
        {
            null or ContentCategory.All => source,
            ContentCategory.Table => source.Where(r => r.Asset.AssetType == FamilyAssetType.LookupTable
                                                     || r.Asset.AssetType == FamilyAssetType.Spreadsheet),
            ContentCategory.Other => source.Where(r => r.Asset.AssetType == FamilyAssetType.Other
                                                    || r.Asset.AssetType == FamilyAssetType.Model3D),
            _ => source.Where(r => r.Asset.AssetType == (FamilyAssetType)(int)SelectedContentCategory.Value)
        };

        var sorted = source.OrderByDescending(r => r.IsPrimary)
                           .ThenByDescending(r => r.Asset.CreatedAtUtc)
                           .ToList();

        FilteredAssets = new ObservableCollection<ContentAssetRow>(sorted);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ChangeAvatar()
    {
        var path = _dialogService.ShowAssetOpenFileDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_Props_SelectImage) ?? "Select image",
            FamilyAssetType.Image);
        if (path is null) return;

        await WithBusyStateAsync(async () =>
        {
            var asset = await _assetService.AddAssetAsync(_catalogItemId, null, FamilyAssetType.Image, path, null);
            await _assetService.SetPrimaryAssetAsync(asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RemoveAvatar()
    {
        if (!HasAvatar || ImageAssets.Count == 0) return;

        var primary = ImageAssets.FirstOrDefault(a => a.IsPrimary) ?? ImageAssets.FirstOrDefault();
        if (primary is null) return;

        await WithBusyStateAsync(async () =>
        {
            await _assetService.DeleteAssetAsync(primary.Id);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    /// <summary>
    /// Unified "Add file" command: opens a single file dialog with all supported
    /// extensions, auto-detects the asset type from the file extension, and adds
    /// the file as shared (not version-bound). After adding, switches the sidebar
    /// to the matching category so the user immediately sees the new file.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task AddFileUnified()
    {
        var path = ShowUnifiedAssetOpenFileDialog();
        if (path is null) return;

        var assetType = FamilyAssetTypeExtensions.DetectFromExtension(path);

        await WithBusyStateAsync(async () =>
        {
            await _assetService.AddAssetAsync(_catalogItemId, null, assetType, path, null);
            await LoadAssetsAsync(CancellationToken.None);
        });

        SelectedContentCategory = assetType switch
        {
            FamilyAssetType.Image => ContentCategory.Image,
            FamilyAssetType.Video => ContentCategory.Video,
            FamilyAssetType.Document => ContentCategory.Document,
            FamilyAssetType.LookupTable or FamilyAssetType.Spreadsheet => ContentCategory.Table,
            _ => ContentCategory.Other
        };
    }

    private string? ShowUnifiedAssetOpenFileDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog();
        dialog.Title = LanguageManager.GetString(StringLocalization.Keys.FM_Props_AddFile) ?? "Select file";
        dialog.Filter = FamilyAssetTypeExtensions.AllAssetFilters();
        dialog.CheckFileExists = true;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task AddAsset(string assetTypeStr)
    {
        if (!Enum.TryParse<FamilyAssetType>(assetTypeStr, out var assetType)) return;

        var path = _dialogService.ShowAssetOpenFileDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_Props_AddFile) ?? "Select file",
            assetType);
        if (path is null) return;

        await WithBusyStateAsync(async () =>
        {
            await _assetService.AddAssetAsync(_catalogItemId, null, assetType, path, null);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task DeleteAsset(ContentAssetRow? row)
    {
        if (row is null) return;
        var asset = row.Asset;

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_Props_ConfirmDeleteAssetTitle) ?? "Delete file";
        var bodyTemplate = LanguageManager.GetString(StringLocalization.Keys.FM_Props_ConfirmDeleteAssetBody)
            ?? "Delete file \"{0}\"? This action is irreversible.";
        var body = string.Format(bodyTemplate, asset.FileName);

        if (!_dialogService.ShowConfirmation(title, body))
        {
            SmartConLogger.Info($"DeleteAsset: user cancelled deletion of '{asset.FileName}'");
            return;
        }

        await WithBusyStateAsync(async () =>
        {
            await _assetService.DeleteAssetAsync(asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    [RelayCommand]
    private async Task OpenAsset(ContentAssetRow? row)
    {
        if (row is null) return;
        var asset = row.Asset;

        var path = await _assetService.ResolveAssetPathAsync(asset.Id);
        if (path is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OpenAsset({asset?.FileName ?? "<null>"}): failed: {ex.Message} [Action: проверьте, что файл существует и не заблокирован другим процессом]");
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task SetAsPrimary(ContentAssetRow? row)
    {
        if (row is null || row.Asset.AssetType != FamilyAssetType.Image) return;

        SmartConLogger.Info($"SetAsPrimary: assetId={row.Asset.Id}, name='{row.Asset.FileName}', triggering LoadAssetsAsync");

        await WithBusyStateAsync(async () =>
        {
            await _assetService.SetPrimaryAssetAsync(row.Asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    /// <summary>
    /// Per-file toggle: switches an asset between shared (all versions) and
    /// per-version (current version only). Moves the physical file and updates
    /// the DB record. Updates only the single row in-place — does NOT call
    /// <see cref="LoadAssetsAsync"/> to avoid destroying/recreating all
    /// ListBoxItem containers (which causes toggle flicker).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ToggleAssetVersionBinding(ContentAssetRow? row)
    {
        if (row is null || !row.CanToggleVersionBinding) return;
        var asset = row.Asset;

        var newLabel = row.IsVersionBound ? null : VersionLabel;

        await WithBusyStateAsync(async () =>
        {
            await _assetService.SetAssetVersionBindingAsync(asset.Id, newLabel);

            var updated = await _assetService.GetAssetsAsync(_catalogItemId, VersionLabel, CancellationToken.None);
            var fresh = updated.FirstOrDefault(a => a.Id == asset.Id);
            if (fresh is not null)
                row.UpdateAsset(fresh);
        });
    }
}
