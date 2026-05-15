using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
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

    private async Task LoadAssetsAsync(CancellationToken ct)
    {
        var assets = await _assetService.GetAssetsAsync(_catalogItemId, ct: ct);

        ImageAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Image));
        VideoAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Video));
        DocumentAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Document));
        LookupAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.LookupTable));
        SpreadsheetAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Spreadsheet));
        Model3DAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Model3D));
        OtherAssets = new ObservableCollection<FamilyAsset>(assets.Where(a => a.AssetType == FamilyAssetType.Other));

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
    private async Task DeleteAsset(FamilyAsset? asset)
    {
        if (asset is null) return;

        await WithBusyStateAsync(async () =>
        {
            await _assetService.DeleteAssetAsync(asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }

    [RelayCommand]
    private async Task OpenAsset(FamilyAsset? asset)
    {
        if (asset is null) return;

        var path = await _assetService.ResolveAssetPathAsync(asset.Id);
        if (path is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OpenAsset failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task SetAsPrimary(FamilyAsset? asset)
    {
        if (asset is null || asset.AssetType != FamilyAssetType.Image) return;

        await WithBusyStateAsync(async () =>
        {
            await _assetService.SetPrimaryAssetAsync(asset.Id);
            await LoadAssetsAsync(CancellationToken.None);
        });
    }
}
