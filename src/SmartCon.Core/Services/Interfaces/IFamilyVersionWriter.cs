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
    /// <param name="catalogItemId">Catalog item that owns this version.</param>
    /// <param name="familyName">Name of the family as it appears in the project.</param>
    /// <param name="versionLabel">Version label (e.g. <c>v2</c>). Null/empty is allowed.</param>
    /// <param name="targetRevit">Revit major version used for <c>SourceRevitVersion</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteVersionMarkerAsync(
        string catalogItemId,
        string familyName,
        string? versionLabel,
        int targetRevit,
        CancellationToken ct);
}
