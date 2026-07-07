using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Row in the "Versions" tab of <see cref="FamilyPropertiesViewModel"/>.
/// Represents a single <c>catalog_versions</c> row (logical version label × Revit version).
/// </summary>
public sealed partial class FamilyVersionRow : ObservableObject
{
    public string VersionId { get; }
    public string VersionLabel { get; }
    public int RevitMajorVersion { get; }
    public int? TypesCount { get; }
    public int? ParametersCount { get; }
    public DateTimeOffset PublishedAtUtc { get; }
    public string PublishedAtText { get; }
    public string? PublishedBy { get; }
    public string? ContentHash { get; }
    public int? HashFormatVersion { get; }

    /// <summary>
    /// Type names that belong to this specific version (loaded via
    /// <c>GetTypesForItemVersionAsync</c>). Used for the tooltip on the
    /// "Types" column (ADR-041 rev #5). Compact: no row-details / banner —
    /// <see cref="TypeNamesTooltip"/> shows the full list on hover.
    /// </summary>
    public IReadOnlyList<string> TypeNames { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Pre-formatted tooltip text for the Types cell. Either:
    /// <list type="bullet">
    /// <item>"— нет типов —" when <see cref="TypeNames"/> is empty</item>
    /// <item>The full list joined by ", " otherwise</item>
    /// </list>
    /// </summary>
    public string TypeNamesTooltip => TypeNames.Count == 0
        ? "— нет типов —"
        : string.Join(", ", TypeNames);

    /// <summary>
    /// Display string for the "Types" column. Shows the actual number of
    /// types found in the version (from <see cref="TypeNames"/>.Count)
    /// rather than the <see cref="TypesCount"/> field from
    /// <c>catalog_versions.types_count</c> — because the latter is NULL
    /// when metadata extraction did not record it (older imports), even
    /// though the actual <c>family_types</c> rows exist. Falls back to
    /// <see cref="TypesCount"/> if the type-name list is itself empty
    /// (which can happen if the lookup failed — defensive). Returns "—"
    /// when both are null/empty.
    /// ADR-041 rev #5.
    /// </summary>
    public string TypesCountDisplay
    {
        get
        {
            if (TypeNames.Count > 0) return TypeNames.Count.ToString(CultureInfo.InvariantCulture);
            if (TypesCount.HasValue) return TypesCount.Value.ToString(CultureInfo.InvariantCulture);
            return "—";
        }
    }

    /// <summary>
    /// Whether <see cref="VersionLabel"/> matches
    /// <c>catalog_items.current_version_label</c> — i.e. this row is the
    /// active version of its catalog item.
    /// </summary>
    [ObservableProperty]
    private bool _isActive;

    public FamilyVersionRow(
        string versionId,
        string versionLabel,
        int revitMajorVersion,
        int? typesCount,
        int? parametersCount,
        DateTimeOffset publishedAtUtc,
        string publishedAtText,
        bool isActive,
        string? contentHash,
        int? hashFormatVersion = null)
        : this(
            versionId,
            versionLabel,
            revitMajorVersion,
            typesCount,
            parametersCount,
            publishedAtUtc,
            publishedAtText,
            isActive,
            contentHash,
            hashFormatVersion,
            publishedBy: null,
            typeNames: Array.Empty<string>())
    {
    }

    /// <summary>
    /// Constructor with pre-loaded <paramref name="typeNames"/> and
    /// <paramref name="publishedBy"/> (ADR-041 rev #5). Allows
    /// <see cref="FamilyPropertiesViewModel.LoadVersionsAsync"/> to populate
    /// the list of type names per version + the author username so the
    /// tooltip + column show them.
    /// </summary>
    public FamilyVersionRow(
        string versionId,
        string versionLabel,
        int revitMajorVersion,
        int? typesCount,
        int? parametersCount,
        DateTimeOffset publishedAtUtc,
        string publishedAtText,
        bool isActive,
        string? contentHash,
        int? hashFormatVersion,
        string? publishedBy,
        IReadOnlyList<string> typeNames)
    {
        VersionId = versionId;
        VersionLabel = versionLabel;
        RevitMajorVersion = revitMajorVersion;
        TypesCount = typesCount;
        ParametersCount = parametersCount;
        PublishedAtUtc = publishedAtUtc;
        PublishedAtText = publishedAtText;
        _isActive = isActive;
        ContentHash = contentHash;
        HashFormatVersion = hashFormatVersion;
        PublishedBy = publishedBy;
        TypeNames = typeNames ?? Array.Empty<string>();
    }

    /// <summary>
    /// Used by <see cref="FamilyPropertiesViewModel.LoadVersionsAsync"/> to
    /// attach the per-version type-name list after the row has been
    /// constructed (e.g. when fetched separately in a batch after the row
    /// collection is built). ADR-041 rev #5.
    /// </summary>
    internal void SetTypeNames(IReadOnlyList<string> typeNames)
    {
        TypeNames = typeNames ?? Array.Empty<string>();
    }

    public override string ToString() => $"{VersionLabel} (R{RevitMajorVersion}){(IsActive ? " *" : "")}";
}
