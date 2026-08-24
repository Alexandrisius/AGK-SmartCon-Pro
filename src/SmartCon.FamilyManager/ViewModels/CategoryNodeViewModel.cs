using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class CategoryNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => true;

    public string CategoryId { get; set; }
    public string? ParentId { get; set; }
    public string FullPath { get; set; }
    public int SortOrder { get; set; }

    [ObservableProperty] private int _familyCount;

    // ── Auto-assignment rules indicator (#241) ─────────────────────────

    /// <summary>Total assignment rule groups of this category (all states).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignmentIconKind))]
    [NotifyPropertyChangedFor(nameof(AssignmentIconBrush))]
    [NotifyPropertyChangedFor(nameof(AssignmentRulesTooltip))]
    private int _assignmentRuleCount;

    /// <summary>Assignment rule groups with IsEnabled = false — drives the
    /// orange state (mirrors the validation shield semantics).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssignmentIconKind))]
    [NotifyPropertyChangedFor(nameof(AssignmentIconBrush))]
    [NotifyPropertyChangedFor(nameof(AssignmentRulesTooltip))]
    private int _disabledAssignmentRuleCount;

    /// <summary>
    /// Filter icon on the category node: gray outline when no rules (still
    /// clickable — opens the editor), green filter-check when all rules
    /// are enabled, orange filter-cog when at least one rule is disabled.
    /// Same three-state semantics as the validation shield.
    /// </summary>
    public string AssignmentIconKind =>
        AssignmentRuleCount == 0 ? "FilterOutline"
        : DisabledAssignmentRuleCount > 0 ? "FilterCog"
        : "FilterCheck";

    public string AssignmentIconBrush =>
        AssignmentRuleCount == 0 ? "#9E9E9E"
        : DisabledAssignmentRuleCount > 0 ? "#FB8C00"
        : "#4CAF50";

    /// <summary>Tooltip of the filter icon (count + click hint).</summary>
    public string AssignmentRulesTooltip
    {
        get
        {
            static string? Loc(string key) => SmartCon.UI.LanguageManager.GetString(key);
            if (AssignmentRuleCount == 0)
            {
                return Loc(SmartCon.UI.StringLocalization.Keys.FM_AssignEditor_TreeNone)
                    ?? "Правила автоназначения не заданы — нажмите для настройки";
            }

            var count = DisabledAssignmentRuleCount > 0
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_AssignEditor_TreeCountDisabled)
                        ?? "Правила автоназначения: {0} (отключено: {1})",
                    AssignmentRuleCount,
                    DisabledAssignmentRuleCount)
                : string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_AssignEditor_TreeCount)
                        ?? "Правила автоназначения: {0}",
                    AssignmentRuleCount);
            var hint = Loc(SmartCon.UI.StringLocalization.Keys.FM_Badge_ClickHint)
                ?? "Нажмите для подробностей";
            return count + " — " + hint;
        }
    }

    /// <summary>Roll-up: true if any leaf under this category is stale (recursive).</summary>
    [ObservableProperty] private bool _hasStale;

    /// <summary>Roll-up: number of stale leaves under this category (recursive).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StaleBadgeTooltip))]
    private int _staleCount;

    // ── Clickable status badge (#210) ──────────────────────────────────

    /// <summary>#210: the category's notices — a single warning while stale leaves exist.</summary>
    [ObservableProperty]
    private IReadOnlyList<StatusNotice> _statusNotices = Array.Empty<StatusNotice>();

    /// <summary>One-line hint for the warning triangle (count + click hint).</summary>
    public string StaleBadgeTooltip
    {
        get
        {
            var count = SmartCon.UI.Converters.StatusTooltipText.ForStaleCount(StaleCount) ?? string.Empty;
            var hint = SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Badge_ClickHint)
                ?? "Нажмите для подробностей";
            return string.IsNullOrEmpty(count) ? hint : count + " — " + hint;
        }
    }

    private void RebuildStatusNotices()
    {
        if (!HasStale)
        {
            StatusNotices = Array.Empty<StatusNotice>();
            return;
        }

        StatusNotices =
        [
            new StatusNotice(
                StatusNoticeSeverity.Warning,
                SmartCon.UI.LanguageManager.GetString(SmartCon.UI.StringLocalization.Keys.FM_Notice_CategoryStale_Title)
                    ?? "В категории есть устаревшие семейства",
                SmartCon.UI.Converters.StatusTooltipText.ForStaleCount(StaleCount) ?? string.Empty),
        ];
    }

    partial void OnHasStaleChanged(bool value) => RebuildStatusNotices();
    partial void OnStaleCountChanged(int value) => RebuildStatusNotices();

    /// <summary>
    /// True if this category or any descendant category is collapsed.
    /// Used by the hover-reveal toggle button in the category header to switch
    /// between "expand" and "collapse" icons. Computed reactively: subscribes
    /// to PropertyChanged of this node and all descendant CategoryNodeViewModels,
    /// recalculates on IsExpanded changes anywhere in the subtree.
    /// </summary>
    [ObservableProperty] private bool _isAnyDescendantCollapsed = true;

    public CategoryNodeViewModel(CategoryNode node)
    {
        CategoryId = node.Id;
        ParentId = node.ParentId;
        DisplayName = node.Name;
        FullPath = node.FullPath;
        SortOrder = node.SortOrder;
        PropertyChanged += OnSelfPropertyChanged;
    }

    public CategoryNodeViewModel(string categoryId, string name, string? parentId, string fullPath)
    {
        CategoryId = categoryId;
        ParentId = parentId;
        DisplayName = name;
        FullPath = fullPath;
        PropertyChanged += OnSelfPropertyChanged;
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsExpanded))
        {
            RecomputeIsAnyDescendantCollapsed();
        }
    }

    private void RecomputeIsAnyDescendantCollapsed()
    {
        var current = ComputeIsAnyDescendantCollapsed();
        if (IsAnyDescendantCollapsed != current)
        {
            IsAnyDescendantCollapsed = current;
        }
    }

    private bool ComputeIsAnyDescendantCollapsed()
    {
        if (!IsExpanded)
            return true;

        foreach (var child in Children)
        {
            if (child is CategoryNodeViewModel catChild && catChild.IsAnyDescendantCollapsed)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Hook called from <see cref="FamilyManagerMainViewModel.BuildCategoryNode"/> after the
    /// subtree is fully assembled. Wires up reactive collapse-state tracking across the
    /// subtree by subscribing to PropertyChanged of every descendant CategoryNodeViewModel
    /// and to Children.CollectionChanged so newly added/removed descendants update tracking.
    /// Ancestors are notified automatically because each parent subscribes to its direct
    /// children — when a deep descendant toggles IsExpanded, the change propagates up the
    /// chain one hop at a time.
    /// </summary>
    internal void AttachCollapseTracking()
    {
        Children.CollectionChanged += OnChildrenCollectionChanged;
        foreach (var child in Children)
        {
            if (child is CategoryNodeViewModel catChild)
                AttachToDescendant(catChild);
        }
        RecomputeIsAnyDescendantCollapsed();
    }

    internal void DetachCollapseTracking()
    {
        Children.CollectionChanged -= OnChildrenCollectionChanged;
        foreach (var child in Children)
        {
            if (child is CategoryNodeViewModel catChild)
                DetachFromDescendant(catChild);
        }
    }

    private void OnChildrenCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems)
            {
                if (item is CategoryNodeViewModel cat)
                    AttachToDescendant(cat);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems)
            {
                if (item is CategoryNodeViewModel cat)
                    DetachFromDescendant(cat);
            }
        }

        RecomputeIsAnyDescendantCollapsed();
    }

    private void AttachToDescendant(CategoryNodeViewModel descendant)
    {
        descendant.PropertyChanged += OnDescendantPropertyChanged;
        descendant.AttachCollapseTracking();
    }

    private void DetachFromDescendant(CategoryNodeViewModel descendant)
    {
        descendant.PropertyChanged -= OnDescendantPropertyChanged;
        descendant.DetachCollapseTracking();
    }

    private void OnDescendantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsExpanded) || e.PropertyName == nameof(IsAnyDescendantCollapsed))
            RecomputeIsAnyDescendantCollapsed();
    }
}
