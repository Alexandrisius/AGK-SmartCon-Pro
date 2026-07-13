using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyTooltipViewModel : ObservableObject
{
    private readonly string _catalogItemId;
    private readonly IFamilyAssetService _assetService;

    [ObservableProperty] private string? _description;
    [ObservableProperty] private bool _hasDescription;
    [ObservableProperty] private System.Windows.Media.Imaging.BitmapImage? _avatarImage;
    [ObservableProperty] private bool _hasAvatar;
    [ObservableProperty] private bool _isLoading;

    private bool _loaded;

    public FamilyTooltipViewModel(string catalogItemId, string? description, IFamilyAssetService assetService)
    {
        if (catalogItemId is null) throw new ArgumentNullException(nameof(catalogItemId));
        if (assetService is null) throw new ArgumentNullException(nameof(assetService));

        _catalogItemId = catalogItemId;
        _assetService = assetService;
        Description = description;
        HasDescription = !string.IsNullOrWhiteSpace(description);
    }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded || IsLoading) return;

        IsLoading = true;
        try
        {
            using var _scope = SmartConLogger.BeginScope("FMTooltip",
                ("Method", nameof(LoadAsync)),
                ("CatalogItemId", _catalogItemId));

            var primary = await _assetService.GetPrimaryImageAsync(_catalogItemId, null, ct).ConfigureAwait(true);
            if (primary is null)
            {
                SmartConLogger.Debug("FamilyTooltipViewModel: no primary image found");
                _loaded = true;
                return;
            }

            var path = await _assetService.ResolveAssetPathAsync(primary.Id, ct).ConfigureAwait(true);
            if (path is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SmartConLogger.Warn($"FamilyTooltipViewModel: resolved path missing for asset '{primary.Id}' [Action: проверьте целостность managed storage и family_assets]");
                _loaded = true;
                return;
            }

            AvatarImage = LoadBitmap(path);
            HasAvatar = AvatarImage is not null;
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"FamilyTooltipViewModel: failed to load avatar for '{_catalogItemId}': {ex.Message} [Action: проверьте smartcon.log и целостность файла изображения]");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static System.Windows.Media.Imaging.BitmapImage? LoadBitmap(string path)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = 240;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"FamilyTooltipViewModel: failed to decode image '{Path.GetFileName(path)}': {ex.Message} [Action: проверьте формат файла]");
            return null;
        }
    }
}
