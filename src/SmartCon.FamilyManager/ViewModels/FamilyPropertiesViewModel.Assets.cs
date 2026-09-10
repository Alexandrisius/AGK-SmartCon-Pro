using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services;
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

        // ADR-047 / #131: single resolution chain — derived avatar.png, else primary image.
        // Decode on every load: avatar.png is rewritten in place on re-crop, so a
        // path-string binding would not notice the change (ADR-047 rev 2 bugfix).
        var avatarPath = await _assetService.GetAvatarImagePathAsync(_catalogItemId, VersionLabel, ct);
        AvatarImage = avatarPath is not null ? AvatarImageLoader.Load(avatarPath, 560) : null;
        HasAvatar = AvatarImage is not null;

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
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;
        var path = _dialogService.ShowAssetOpenFileDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_Props_SelectImage) ?? "Select image",
            FamilyAssetType.Image);
        if (path is null) return;

        // #131: let the user pick the crop area before the image becomes the avatar.
        // Cancelling the dialog aborts the whole operation — no asset is added.
        var crop = ShowCropDialog(path);
        if (crop is null) return;

        await WithBusyStateAsync(async () =>
        {
            var asset = await _assetService.AddAssetAsync(_catalogItemId, null, FamilyAssetType.Image, path, null);
            await _assetService.SetPrimaryAssetAsync(asset.Id);
            await SaveAvatarFromCropAsync(crop, CancellationToken.None);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task RemoveAvatar()
    {
        if (!HasAvatar) return;
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;

        // ADR-047: removes the derived avatar + primary flag; the source image
        // assets themselves are kept (they can be deleted individually from the list).
        await WithBusyStateAsync(async () =>
        {
            await _assetService.ClearAvatarAsync(_catalogItemId, CancellationToken.None);
            AvatarChanged?.Invoke();
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
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;
        var path = ShowUnifiedAssetOpenFileDialog();
        if (path is null) return;

        var assetType = FamilyAssetTypeExtensions.DetectFromExtension(path);

        // #131: the very first user image becomes the de-facto avatar — offer to crop it.
        // Auto-extracted GLB previews don't count (hidden technical assets).
        var crop = assetType == FamilyAssetType.Image && !ImageAssets.Any(a => !IsAutoExtractedPreview(a))
            ? ShowCropDialog(path)
            : null;

        await WithBusyStateAsync(async () =>
        {
            var asset = await _assetService.AddAssetAsync(_catalogItemId, null, assetType, path, null);
            if (crop is not null)
            {
                await _assetService.SetPrimaryAssetAsync(asset.Id);
                await SaveAvatarFromCropAsync(crop, CancellationToken.None);
            }
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
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;

        var path = _dialogService.ShowAssetOpenFileDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_Props_AddFile) ?? "Select file",
            assetType);
        if (path is null) return;

        // #131: the very first user image becomes the de-facto avatar — offer to crop it.
        // Auto-extracted GLB previews don't count (hidden technical assets).
        var crop = assetType == FamilyAssetType.Image && !ImageAssets.Any(a => !IsAutoExtractedPreview(a))
            ? ShowCropDialog(path)
            : null;

        await WithBusyStateAsync(async () =>
        {
            var asset = await _assetService.AddAssetAsync(_catalogItemId, null, assetType, path, null);
            if (crop is not null)
            {
                await _assetService.SetPrimaryAssetAsync(asset.Id);
                await SaveAvatarFromCropAsync(crop, CancellationToken.None);
            }
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task DeleteAsset(ContentAssetRow? row)
    {
        if (row is null) return;
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;
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

        // ADR-047 rev 5: deleting the primary image also deleted the derived avatar —
        // notify long-lived consumers (tree tooltip) to drop their cache.
        if (asset.AssetType == FamilyAssetType.Image && asset.IsPrimary)
            AvatarChanged?.Invoke();
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
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;

        // #131: pick the crop area for the new avatar before marking primary.
        var path = await _assetService.ResolveAssetPathAsync(row.Asset.Id);
        if (path is null)
        {
            SmartConLogger.Warn($"SetAsPrimary: resolved path missing for asset '{row.Asset.Id}' [Action: проверьте целостность managed storage и family_assets]");
            return;
        }

        var crop = ShowCropDialog(path);
        if (crop is null) return;

        SmartConLogger.Info($"SetAsPrimary: assetId={row.Asset.Id}, name='{row.Asset.FileName}', applying cropped avatar");

        await WithBusyStateAsync(async () =>
        {
            await _assetService.SetPrimaryAssetAsync(row.Asset.Id);
            await SaveAvatarFromCropAsync(crop, CancellationToken.None);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    /// <summary>
    /// Saves the rendered crop (temp PNG from the crop dialog) as the family avatar
    /// and removes the temp file afterwards.
    /// </summary>
    private async Task SaveAvatarFromCropAsync(string cropTempPath, CancellationToken ct)
    {
        try
        {
            await _assetService.SaveAvatarAsync(_catalogItemId, cropTempPath, ct);
            AvatarChanged?.Invoke();
        }
        finally
        {
            try { if (File.Exists(cropTempPath)) File.Delete(cropTempPath); } catch { }
        }
    }

    /// <summary>
    /// Opens the avatar crop dialog (issue #131) for the given image file.
    /// Returns the path of the rendered 560×420 PNG (temp file) when the user
    /// applied the crop; null when cancelled or when the image cannot be opened/rendered.
    /// </summary>
    private string? ShowCropDialog(string imagePath)
    {
        int pixelWidth;
        int pixelHeight;
        try
        {
            (pixelWidth, pixelHeight) = _avatarCropService.GetImageDimensions(imagePath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"ShowCropDialog: failed to read image '{Path.GetFileName(imagePath)}': {ex.Message} [Action: проверьте, что файл изображения не повреждён и доступен для чтения]");
            var openTitle = LanguageManager.GetString(StringLocalization.Keys.FM_Crop_Title) ?? "Crop avatar";
            var openMessage = LanguageManager.GetString(StringLocalization.Keys.FM_Crop_OpenError) ?? "Failed to open the image.";
            _dialogService.ShowError(openTitle, openMessage);
            return null;
        }

        var vm = new CropAvatarViewModel(imagePath, pixelWidth, pixelHeight, _avatarCropService);
        var result = _dialogService.ShowAvatarCropper(vm);
        if (result == true && vm.ResultPath is not null)
            return vm.ResultPath;

        if (vm.ApplyError is not null)
        {
            var renderTitle = LanguageManager.GetString(StringLocalization.Keys.FM_Crop_Title) ?? "Crop avatar";
            var renderMessage = LanguageManager.GetString(StringLocalization.Keys.FM_Crop_RenderError) ?? "Failed to crop the image.";
            _dialogService.ShowError(renderTitle, renderMessage);
        }

        return null;
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
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;
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
