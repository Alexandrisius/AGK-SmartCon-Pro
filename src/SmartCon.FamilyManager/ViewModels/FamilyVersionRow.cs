using System;
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
    public string? ContentHash { get; }
    public int? HashFormatVersion { get; }

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
    }

    public override string ToString() => $"{VersionLabel} (R{RevitMajorVersion}){(IsActive ? " *" : "")}";
}
