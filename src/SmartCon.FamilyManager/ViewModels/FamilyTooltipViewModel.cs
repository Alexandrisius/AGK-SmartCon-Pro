using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
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

            // ADR-047 / #131: unified resolution chain — derived avatar.png, else primary image.
            var path = await _assetService.GetAvatarImagePathAsync(_catalogItemId, null, ct).ConfigureAwait(true);
            if (path is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SmartConLogger.Debug("FamilyTooltipViewModel: no avatar image found");
                AvatarImage = null;
                HasAvatar = false;
                _loaded = true;
                return;
            }

            AvatarImage = AvatarImageLoader.Load(path, 560);
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

    /// <summary>
    /// Drops the loaded-state cache and reloads the avatar (ADR-047 rev 2: called when
    /// the avatar was re-cropped or removed while this tree node is still alive —
    /// otherwise the tooltip would show the stale image until the next tree reload).
    /// </summary>
    public void Invalidate()
    {
        _loaded = false;
        _ = LoadAsync(CancellationToken.None);
    }
}
