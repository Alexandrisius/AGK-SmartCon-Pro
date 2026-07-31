using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyTypeNodeViewModel : CatalogTreeNodeViewModel
{
    public override bool IsCategory => false;
    public override bool IsType => true;

    public string CatalogItemId { get; }
    public string TypeName { get; }
    public bool IsVirtual { get; }
    public string FamilySource { get; }
    public string? UniqueId { get; }
    public bool IsUnavailable { get; }

    /// <summary>
    /// Issue #183: Revit system family of the type (from
    /// <c>family_types.family_name</c>) — the sync identity is
    /// (family, name). null for legacy rows and loadable types.
    /// </summary>
    public string? FamilyName { get; }

    /// <summary>
    /// #187: the type is present in the ACTIVE project (same family + name
    /// found in the document). Computed on every tree load via one
    /// CollectTypes pass over the system categories of the catalog.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceState))]
    private bool _isInProject;

    /// <summary>
    /// #187: the type is present in the ACTIVE project AND its ES marker
    /// does not match the catalog (outdated) — the orange dot. Meaningful
    /// only for system types; always false for loadable (their stale badge
    /// lives on the leaf).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceState))]
    private bool _isStaleInProject;

    /// <summary>#187: tri-state presence for the dot indicator.</summary>
    public TypePresenceState PresenceState =>
        !IsInProject
            ? TypePresenceState.NotInProject
            : IsStaleInProject
                ? TypePresenceState.StaleInProject
                : TypePresenceState.InProject;

    public bool IsSystemType =>
        FamilySource == "system" || (UniqueId is not null && UniqueId.Length > 0);

    public FamilyTypeNodeViewModel(
        string catalogItemId,
        string typeName,
        bool isVirtual = false,
        string familySource = "loadable",
        string? uniqueId = null,
        string? displayName = null,
        bool isUnavailable = false,
        string? familyName = null)
    {
        CatalogItemId = catalogItemId;
        TypeName = typeName;
        IsVirtual = isVirtual;
        FamilySource = familySource;
        UniqueId = uniqueId;
        IsUnavailable = isUnavailable;
        FamilyName = familyName;
        DisplayName = displayName ?? typeName;
    }
}
