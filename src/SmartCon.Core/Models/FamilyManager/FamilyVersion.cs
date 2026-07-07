namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Per-family version marker stored in ExtensibleStorage
/// (Schema <c>SmartCon.FamilyVersion.v1</c>, ADR-030).
/// </summary>
/// <param name="SchemaVersion">Schema version of the payload (always 1 today).</param>
/// <param name="CatalogItemId">Catalog item that owns this family version.</param>
/// <param name="VersionLabel">Version label (e.g. <c>v1</c>) that was loaded into Revit.</param>
/// <param name="LoadedAtUtc">UTC timestamp when the family was loaded into the project.</param>
/// <param name="SourceRevitVersion">Revit major version used to load the family.</param>
public sealed record FamilyVersion(
    int SchemaVersion,
    string CatalogItemId,
    string VersionLabel,
    DateTimeOffset LoadedAtUtc,
    int SourceRevitVersion)
{
    public const int CurrentSchemaVersion = 1;

    public static FamilyVersion Empty { get; } =
        new(0, string.Empty, string.Empty, DateTimeOffset.MinValue, 0);
}
