namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of loading a family into a Revit project.
/// </summary>
/// <param name="Success">Whether the load succeeded.</param>
/// <param name="FamilyName">Loaded family name.</param>
/// <param name="Message">Informational message.</param>
/// <param name="ErrorMessage">Error message if failed.</param>
/// <param name="Status">Detailed status of the load operation.</param>
public sealed record FamilyLoadResult(
    bool Success,
    string? FamilyName,
    string? Message,
    string? ErrorMessage,
    FamilyLoadStatus Status = FamilyLoadStatus.Failed);
