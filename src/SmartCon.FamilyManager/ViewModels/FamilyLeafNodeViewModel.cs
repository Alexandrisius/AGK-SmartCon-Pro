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

    // ── Catalog compliance (#259, «Проверить → Правила») ────────────────
    // Verdict of the last session compliance check: catalog item vs the
    // effective rules of its category. Deliberately NOT a StaleReason — the
    // «Обновить» button applies catalog content to the project and cannot
    // fix a rule violation (the fix is edit + re-import).

    /// <summary>#259: compliance verdict of the session «Проверить → Правила» run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleViolations))]
    [NotifyPropertyChangedFor(nameof(IsComplianceUnverifiable))]
    [NotifyPropertyChangedFor(nameof(HasProblemBadge))]
    [NotifyPropertyChangedFor(nameof(ProblemBadgeTooltip))]
    [NotifyPropertyChangedFor(nameof(RuleBadgeTooltip))]
    private ComplianceStatus _complianceStatus = ComplianceStatus.NotChecked;

    /// <summary>#259: number of rule violations of the Fail verdict (drives the notice title).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleViolations))]
    [NotifyPropertyChangedFor(nameof(RuleBadgeTooltip))]
    private int _ruleViolationCount;

    // ── Routing phantoms (#133, «Очистить недоступные записи») ────────
    // A routing rule of this family references a "Family:Type" token whose
    // family no longer exists in the catalog (purged as missing / deleted).
    // The badge opens the properties directly on the Routing tab at the
    // affected type — the fix is to pick a replacement fitting there.

    /// <summary>#133: at least one routing rule references a family missing from the catalog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutingIssuesBadgeTooltip))]
    private bool _hasRoutingIssues;

    /// <summary>#133: display names of the missing fitting families (tooltip lines).</summary>
    [ObservableProperty]
    private IReadOnlyList<string>? _missingRoutingFamilies;

    /// <summary>One-line hint for the routing-phantom badge.</summary>
    public string RoutingIssuesBadgeTooltip
    {
        get
        {
            var title = SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_RoutingPhantom_Short)
                ?? "Трассировка: фитинг отсутствует в каталоге";
            var hint = SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_RoutingPhantom_Hint)
                ?? "Нажмите, чтобы открыть вкладку «Трассировка»";
            return title + " — " + hint;
        }
    }

    /// <summary><c>true</c> when the red shield badge is shown (Fail verdict).</summary>
    public bool HasRuleViolations => ComplianceStatus == ComplianceStatus.Fail && RuleViolationCount > 0;

    /// <summary><c>true</c> when the rules could not be evaluated (no extraction
    /// data) — rides the existing orange problem triangle, guidance «Обновить базу».</summary>
    public bool IsComplianceUnverifiable => ComplianceStatus == ComplianceStatus.CannotVerify;

    /// <summary>One-line hint for the red rule-violation shield (the violation
    /// table lives in the validation report behind the details dialog).</summary>
    public string RuleBadgeTooltip
    {
        get
        {
            var count = SmartCon.UI.Converters.StatusTooltipText.ForRuleViolationCount(RuleViolationCount) ?? string.Empty;
            var hint = SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_ClickHint)
                ?? "Нажмите для подробностей";
            return string.IsNullOrEmpty(count) ? hint : count + " — " + hint;
        }
    }

    // ── Clickable status badges (#210) ─────────────────────────────────
    // The leaf shows at most two clickable badges (StatusBadgeButton
    // style): the paperclip (used-as-dependency info) and ONE problem
    // triangle (stale and/or outdated nested). Both open the status
    // details dialog listing all notices with the full texts that used
    // to be long tooltips.

    /// <summary>#210: active status notices of this leaf (warnings first, info last).</summary>
    [ObservableProperty]
    private IReadOnlyList<StatusNotice> _statusNotices = Array.Empty<StatusNotice>();

    /// <summary><c>true</c> when the problem triangle is shown (stale, outdated
    /// nested and/or unverifiable rules — #259 CannotVerify rides the same
    /// warning badge; Fail has its own red shield).</summary>
    public bool HasProblemBadge => IsStale || HasOutdatedDependencies || IsComplianceUnverifiable;

    /// <summary>One-line hint for the problem triangle (full texts live in the details dialog).</summary>
    public string ProblemBadgeTooltip
    {
        get
        {
            var parts = new List<string>(3);
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
            if (IsComplianceUnverifiable)
            {
                parts.Add(SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_RulesUnverifiable_Short)
                    ?? "Нет данных для правил");
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
        if (HasRuleViolations)
        {
            // #259: Error — the import gate would block this family today; the
            // fix is edit + re-import, NOT «Обновить» (that applies catalog
            // content to the project and cannot repair a rule violation).
            var title = RuleViolationCount == 1
                ? Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_RuleViolations_TitleOne,
                    "Не соответствует правилам категории (1 нарушение)")
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_RuleViolations_TitleMany,
                        "Не соответствует правилам категории ({0} нарушений)"),
                    RuleViolationCount);
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Error,
                title,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_RuleViolations_Guidance,
                    "Исправьте семейство и импортируйте новую версию — гейт импорта перевалидирует его по действующим правилам.")));
        }
        if (IsComplianceUnverifiable)
        {
            list.Add(new StatusNotice(
                StatusNoticeSeverity.Warning,
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_RuleCannotVerify_Title,
                    "Правила нельзя проверить: нет данных атрибутов"),
                Loc(SmartCon.UI.StringLocalization.Keys.FM_Notice_RuleCannotVerify_Guidance,
                    "Выполните «Обновить базу», чтобы извлечь атрибуты, и повторите проверку.")));
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
        AddRoutingPhantomNotice(list);
        StatusNotices = list;
    }

    partial void OnHasRoutingIssuesChanged(bool value) => RebuildStatusNotices();
    partial void OnMissingRoutingFamiliesChanged(IReadOnlyList<string>? value) => RebuildStatusNotices();

    partial void OnIsStaleChanged(bool value) => RebuildStatusNotices();
    partial void OnStaleReasonChanged(StaleReason value) => RebuildStatusNotices();
    partial void OnIsDependencyReferencedChanged(bool value) => RebuildStatusNotices();
    partial void OnDependencyReferencedLinesChanged(IReadOnlyList<string>? value) => RebuildStatusNotices();
    partial void OnHasOutdatedDependenciesChanged(bool value) => RebuildStatusNotices();
    partial void OnOutdatedDependencyLinesChanged(IReadOnlyList<string>? value) => RebuildStatusNotices();
    partial void OnComplianceStatusChanged(ComplianceStatus value) => RebuildStatusNotices();
    partial void OnRuleViolationCountChanged(int value) => RebuildStatusNotices();

    private void AddRoutingPhantomNotice(List<StatusNotice> list)
    {
        if (!HasRoutingIssues) return;
        list.Add(new StatusNotice(
            StatusNoticeSeverity.Warning,
            SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Notice_RoutingPhantom_Title)
                ?? "Трассировка ссылается на отсутствующий фитинг",
            SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Notice_RoutingPhantom_Guidance)
                ?? "Фитинг удалён из каталога, поэтому в трассировку он больше не подгрузится. Нажмите значок трассировки у семейства, чтобы открыть вкладку «Трассировка», и выберите замену — либо удалите правило.",
            MissingRoutingFamilies));
    }

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
