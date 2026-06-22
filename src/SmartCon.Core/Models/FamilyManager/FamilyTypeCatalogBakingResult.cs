namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of baking a Type Catalog (.txt) into a Revit family (.rfa).
/// </summary>
/// <param name="Success">True when the output .rfa was created with all catalog types baked in.</param>
/// <param name="OutputRfaPath">Absolute path to the baked .rfa, or null on failure.</param>
/// <param name="BakedTypeCount">Number of types successfully created from the catalog.</param>
/// <param name="ErrorMessage">Human-readable error message when Success is false.</param>
public sealed record FamilyTypeCatalogBakingResult(
    bool Success,
    string? OutputRfaPath,
    int BakedTypeCount,
    string? ErrorMessage);
