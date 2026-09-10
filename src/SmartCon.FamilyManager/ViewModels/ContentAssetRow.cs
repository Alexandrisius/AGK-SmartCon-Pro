using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// UI wrapper for <see cref="FamilyAsset"/> in the Content tab. Implements
/// <see cref="INotifyPropertyChanged"/> so individual properties can be updated
/// in-place without replacing the entire collection (which would cause WPF to
/// destroy and recreate all ListBoxItem containers — producing toggle flicker).
/// </summary>
public sealed class ContentAssetRow : INotifyPropertyChanged
{
    public FamilyAsset Asset { get; private set; }
    public string FileName => Asset.FileName;
    public string? ResolvedPath { get; }
    public string SizeText { get; }
    public bool IsImage => Asset.AssetType == FamilyAssetType.Image;
    public bool HasThumbnail => IsImage && !string.IsNullOrEmpty(ResolvedPath);

    private bool _isPrimary;
    public bool IsPrimary
    {
        get => _isPrimary;
        set { if (_isPrimary != value) { _isPrimary = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSetPrimary)); } }
    }

    public bool CanSetPrimary => IsImage && !_isPrimary;

    private bool _isVersionBound;
    /// <summary>True if this asset is bound to a specific version (VersionLabel != null).</summary>
    public bool IsVersionBound
    {
        get => _isVersionBound;
        set { if (_isVersionBound != value) { _isVersionBound = value; OnPropertyChanged(); } }
    }

    /// <summary>True if the version-binding toggle is available (only when a version context exists).</summary>
    public bool CanToggleVersionBinding { get; }

    public ContentAssetRow(FamilyAsset asset, string? resolvedPath, bool canToggleVersionBinding)
    {
        Asset = asset;
        ResolvedPath = resolvedPath;
        CanToggleVersionBinding = canToggleVersionBinding;
        _isPrimary = asset.IsPrimary;
        _isVersionBound = !string.IsNullOrEmpty(asset.VersionLabel);
        SizeText = FormatSize(asset.SizeBytes);
    }

    /// <summary>
    /// Updates the underlying <see cref="Asset"/> record and derived properties
    /// in-place (fires PropertyChanged) without recreating the row.
    /// </summary>
    public void UpdateAsset(FamilyAsset newAsset)
    {
        Asset = newAsset;
        IsVersionBound = !string.IsNullOrEmpty(newAsset.VersionLabel);
        IsPrimary = newAsset.IsPrimary;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
