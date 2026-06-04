namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Extracts type parameters (not instance parameters) from system family types
/// in a clean .rvt project file (one created by ISystemFamilyRevitOperations.CreateCleanProjectWithTypes).
/// </summary>
public interface ISystemFamilyAttributeExtractionService
{
    /// <summary>
    /// Opens the given .rvt file as a background document, iterates over the
    /// user-selected ElementType instances (matched by name), extracts Type
    /// parameters per type, and returns a FamilyExtractionResult.
    /// The implementation MUST close the background document.
    /// </summary>
    /// <param name="rvtFilePath">Absolute path to the .rvt file.</param>
    /// <param name="typeNames">
    /// Names of ElementTypes to extract (case-insensitive). If null/empty,
    /// the service will try to extract from any type that is not a Revit
    /// built-in default type (a heuristic fallback).
    /// </param>
    FamilyExtractionResult ExtractFromRvt(string rvtFilePath, IReadOnlyList<string>? typeNames);
}
