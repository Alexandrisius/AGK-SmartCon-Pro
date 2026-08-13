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
    [NotifyPropertyChangedFor(nameof(HasProblemBadge))]
    [NotifyPropertyChangedFor(nameof(ProblemBadgeTooltip))]
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

    /// <summary>
    /// E5 (#213, ADR-067): at least one version of at least one parent
    /// references this item in <c>family_dependencies</c> — the paperclip
    /// indicator. Such an item cannot be deleted (dependency guard).
    /// Computed batch-wise on tree load (one reverse query for all leaves).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DependencyReferencedBadgeTooltip))]
    private bool _isDependencyReferenced;

    /// <summary>Lines «Родитель (версии)» for the paperclip badge details.</summary>
    [ObservableProperty]
    private IReadOnlyList<string>? _dependencyReferencedLines;

    /// <summary>
    /// E2 (#209, V30): at least one dependency embedded in this item's
    /// CURRENT version is no longer the child's active version — the
    /// problem badge (distinct from stale: the fix is to re-import THIS
    /// family with the up-to-date nested content). Computed batch-wise on
    /// tree load together with the paperclip.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblemBadge))]
    [NotifyPropertyChangedFor(nameof(ProblemBadgeTooltip))]
    private bool _hasOutdatedDependencies;

    /// <summary>Per-child lines «Фланец (зашита v1, активна v2)» for the outdated-deps badge details.</summary>
    [ObservableProperty]
    private IReadOnlyList<string>? _outdatedDependencyLines;

    // ── Clickable status badges (#210) ─────────────────────────────────
    // The leaf shows at most two clickable badges (StatusBadgeButton
    // style): the paperclip (used-as-dependency info) and ONE problem
    // triangle (stale and/or outdated nested). Both open the status
    // details dialog listing all notices with the full texts that used
    // to be long tooltips.

    /// <summary>#210: active status notices of this leaf (warnings first, info last).</summary>
    [ObservableProperty]
    private IReadOnlyList<StatusNotice> _statusNotices = Array.Empty<StatusNotice>();

    /// <summary><c>true</c> when the problem triangle is shown (stale and/or outdated nested).</summary>
    public bool HasProblemBadge => IsStale || HasOutdatedDependencies;

    /// <summary>One-line hint for the problem triangle (full texts live in the details dialog).</summary>
    public string ProblemBadgeTooltip
    {
        get
        {
            var parts = new List<string>(2);
            if (IsStale)
            {
                parts.Add(SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Stale)
                    ?? "Устарело");
            }
            if (HasOutdatedDependencies)
            {
                parts.Add(SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_OutdatedDeps_Short)
                    ?? "Устарели вложенные");
            }
            var hint = SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_ClickHint)
                ?? "Нажмите для подробностей";
            return string.Join("; ", parts) + " — " + hint;
        }
    }

    /// <summary>One-line hint for the paperclip badge (reference list lives in the details dialog).</summary>
    public string DependencyReferencedBadgeTooltip
    {
        get
        {
            var title = SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Notice_DependencyReferenced_Title)
                ?? "Используется как зависимость";
            var hint = SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_ClickHint)
                ?? "Нажмите для подробностей";
            return title + " — " + hint;
        }
    }

    private void RebuildStatusNotices()
    {
        static string Loc(string key, string fallback) =>
            SmartCon.UI.LanguageManager.GetString(key) ?? fallback;

        var list = new List<StatusNotice>();
        if (IsStale)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Warning,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_Stale_Title, "Семейство устарело"),
                SmartCon.UI.Converters.StatusTooltipText.ForStaleReason(StaleReason)));
        }
        if (HasOutdatedDependencies)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Warning,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_OutdatedDependencies_Title,
                    "Вложенные семейства устарели"),
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_OutdatedDeps_Guidance,
                    "Откройте семейство, перетащите актуальные вложенные версии из каталога и переимпортируйте его с новой версией."),
                OutdatedDependencyLines));
        }
        if (IsDependencyReferenced)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Info,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_DependencyReferenced_Title,
                    "Используется как зависимость"),
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_DependencyRef_Guard,
                    "Семейство нельзя удалить из каталога, пока оно используется как зависимость."),
                DependencyReferencedLines));
        }
        StatusNotices = list;
    }

    partial void OnIsStaleChanged(bool value) => RebuildStatusNotices();
    partial void OnStaleReasonChanged(StaleReason value) => RebuildStatusNotices();
    partial void OnIsDependencyReferencedChanged(bool value) => RebuildStatusNotices();
    partial void OnDependencyReferencedLinesChanged(IReadOnlyList<string>? value) => RebuildStatusNotices();
    partial void OnHasOutdatedDependenciesChanged(bool value) => RebuildStatusNotices();
    partial void OnOutdatedDependencyLinesChanged(IReadOnlyList<string>? value) => RebuildStatusNotices();

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
        RebuildStatusNotices();
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
