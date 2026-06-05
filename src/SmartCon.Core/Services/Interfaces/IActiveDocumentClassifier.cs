namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Classifies the active Revit document to drive the "Import Active File"
/// workflow.
/// </summary>
public enum ActiveDocumentKind
{
    /// <summary>No active document (Revit is starting, no project open, etc.).</summary>
    None,

    /// <summary>Active document is a <c>.rfa</c> family document.</summary>
    Family,

    /// <summary>Active document is a project (<c>.rvt</c>) or a system-family-only .rvt.</summary>
    Project
}

/// <summary>
/// Distinguishes between family documents and project documents in the
/// active Revit session. Used by the "Import Active File" command to pick
/// the correct code path.
/// </summary>
public interface IActiveDocumentClassifier
{
    /// <summary>
    /// Returns the kind of the currently active document. Must be called
    /// inside an <see cref="IFamilyManagerAwaitableEvent"/> callback to be
    /// safe (I-01).
    /// </summary>
    Task<ActiveDocumentKind> ClassifyAsync(CancellationToken ct = default);
}
