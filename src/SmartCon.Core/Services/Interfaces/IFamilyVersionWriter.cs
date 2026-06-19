namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Persists a fresh <c>FamilyVersion</c> marker (ADR-030) on the loaded Revit
/// <c>Family</c> element. Shared between the regular Load/Place path and the
/// stale-detection batch updater so the ES write stays in one place.
/// </summary>
public interface IFamilyVersionWriter
{
    /// <summary>
    /// Build a <see cref="Models.FamilyManager.FamilyVersion"/> and write it as
    /// ExtensibleStorage on the loaded family (matched by name). No-op if the
    /// family is not currently loaded in the active project.
    /// </summary>
    /// <param name="catalogItemId">Catalog item that owns this version. Required.</param>
    /// <param name="familyName">Name of the family as it appears in the project. Required.
    /// Matched case-<strong>in</strong>sensitively against the project (see
    /// <see cref="IFamilyFinder.FindByName"/>).</param>
    /// <param name="versionLabel">Version label (e.g. <c>v2</c>). Null/empty is allowed
    /// and results in an empty string being stored.</param>
    /// <param name="targetRevit">Revit major version used for <c>SourceRevitVersion</c>.
    /// Should be the year the family was saved in (e.g. <c>2024</c>), not the
    /// current Revit year.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalogItemId"/> or <paramref name="familyName"/> is null.
    /// </exception>
    /// <remarks>
    /// This method does NOT throw if the family is not found in the project —
    /// it logs an <c>Info</c> and returns. The caller (StaleFamilyUpdater,
    /// FamilyPlacementDropHandler, LoadPlace) treats a missing family as a
    /// "skip, try again later" case rather than a fatal error.
    /// </remarks>
    Task WriteVersionMarkerAsync(
        string catalogItemId,
        string familyName,
        string? versionLabel,
        int targetRevit,
        CancellationToken ct);
}
