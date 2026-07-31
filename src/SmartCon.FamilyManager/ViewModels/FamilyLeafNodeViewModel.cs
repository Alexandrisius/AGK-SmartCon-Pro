using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyLeafNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;

    public string CatalogItemId { get; }
    public string? CategoryId { get; }
    public string? CategoryPath { get; }
    public string? Manufacturer { get; }
    public ContentStatus ContentStatus { get; }
    public string? VersionLabel { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public IReadOnlyList<string> Tags { get; }
    public string? Description { get; }
    public string FamilySource { get; }
    public string? RevitCategory { get; }
    public FamilyTooltipViewModel TooltipViewModel { get; }

    public IReadOnlyList<string> MatchedTags { get; }
    public bool HasMatchedTags => MatchedTags.Count > 0;

    public int? ActiveRevitMajorVersion { get; }
    public int? MinRevitMajorVersion { get; }
    public int CurrentRevitVersion { get; }

    public bool IsDeprecated => ContentStatus != ContentStatus.Active;

    public bool IsRevitIncompatible =>
        ActiveRevitMajorVersion.HasValue
        && CurrentRevitVersion > 0
        && ActiveRevitMajorVersion.Value > CurrentRevitVersion;

    public bool IsUnavailable => IsDeprecated || IsRevitIncompatible;

    public FamilyUnavailableReason UnavailableReason =>
        (IsDeprecated, IsRevitIncompatible) switch
        {
            (true, true) => FamilyUnavailableReason.DeprecatedAndRevitVersion,
            (true, false) => FamilyUnavailableReason.Deprecated,
            (false, true) => FamilyUnavailableReason.RevitVersion,
            _ => FamilyUnavailableReason.None,
        };

    public int? RequiredRevitVersion => IsRevitIncompatible ? ActiveRevitMajorVersion : null;

    public bool HasCompatibleVersion =>
        MinRevitMajorVersion.HasValue
        && CurrentRevitVersion > 0
        && MinRevitMajorVersion.Value <= CurrentRevitVersion;

    [ObservableProperty]
    private bool _isStale;

    [ObservableProperty]
    private StaleReason _staleReason;

    /// <summary>
    /// #187: the family is loaded in the ACTIVE project (a FamilySymbol of
    /// the same family name exists in the document). Computed on every tree
    /// load via one FamilySymbol collector pass.
    /// </summary>
    [ObservableProperty]
    private bool _isInProject;

    public FamilyLeafNodeViewModel(
        FamilyCatalogItemRow row,
        IFamilyAssetService assetService,
        bool isStale = false,
        StaleReason staleReason = StaleReason.None,
        int currentRevitVersion = 0,
        string? searchText = null)
    {
        CatalogItemId = row.Id;
        CategoryId = row.CategoryId;
        CategoryPath = row.CategoryName;
        DisplayName = row.Name;
        Manufacturer = row.Manufacturer;
        ContentStatus = row.ContentStatus;
        VersionLabel = row.VersionLabel;
        UpdatedAtUtc = row.UpdatedAtUtc;
        Tags = row.Tags;
        Description = row.Description;
        FamilySource = row.FamilySource;
        RevitCategory = row.RevitCategory;
        TooltipViewModel = new FamilyTooltipViewModel(row.Id, row.Description, assetService);
        ActiveRevitMajorVersion = row.ActiveRevitMajorVersion;
        MinRevitMajorVersion = row.MinRevitMajorVersion;
        CurrentRevitVersion = currentRevitVersion;
        _isStale = isStale;
        _staleReason = staleReason;
        MatchedTags = ComputeMatchedTags(row.Name, row.Tags, searchText);
    }

    private static IReadOnlyList<string> ComputeMatchedTags(
        string name, IReadOnlyList<string> tags, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText) || tags.Count == 0)
            return [];

        var tokens = FamilySearchNormalizer.Tokenize(searchText!);
        if (tokens.Count == 0)
            return [];

        var normalizedName = FamilySearchNormalizer.Normalize(name);
        var tagOnlyTokens = tokens.Where(t => !normalizedName.Contains(t)).ToList();
        if (tagOnlyTokens.Count == 0)
            return [];

        return tags
            .Where(tag =>
            {
                var normalizedTag = FamilySearchNormalizer.Normalize(tag);
                return tagOnlyTokens.Any(t => normalizedTag.Contains(t));
            })
            .Take(3)
            .ToList();
    }
}
