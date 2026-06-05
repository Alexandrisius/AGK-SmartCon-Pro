namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of preparing the active Revit family document for import.
/// Returned by <see cref="SmartCon.Core.Services.Interfaces.IActiveFamilyFilePreparer"/>.
/// </summary>
/// <param name="TempRfaPath">
/// Absolute path to the .rfa saved into the temp staging folder
/// (e.g. <c>%TEMP%\SmartCon\FMLoad\{guid}\{name}.rfa</c>).
/// The file is safe to consume by <c>IFamilyImportService</c>.
/// </param>
/// <param name="TempTxtPath">
/// Absolute path to the Type Catalog (.txt) sidecar copied next to
/// <paramref name="TempRfaPath"/>, or <c>null</c> when no sidecar was found
/// next to the original .rfa.
/// </param>
/// <param name="OriginalRfaPath">
/// Absolute path to the original .rfa on disk (managed storage or user folder).
/// <c>null</c> for documents that have never been saved (untitled family).
/// </param>
/// <param name="OriginalTxtPath">
/// Absolute path to the original Type Catalog sidecar found next to
/// <paramref name="OriginalRfaPath"/>, or <c>null</c> when no sidecar exists.
/// </param>
public sealed record ActiveFamilyPreparationResult(
    string TempRfaPath,
    string? TempTxtPath,
    string? OriginalRfaPath,
    string? OriginalTxtPath);
