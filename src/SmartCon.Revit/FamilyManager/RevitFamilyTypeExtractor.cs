using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реализация IFamilyTypeExtractor через Revit API.
/// Использует LoadFamily внутри RunAndRollback для временной загрузки.
/// </summary>
public sealed class RevitFamilyTypeExtractor : IFamilyTypeExtractor
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _transactionService;
    private readonly IFamilyLoadOptionsFactory _loadOptionsFactory;

    public RevitFamilyTypeExtractor(
        IRevitContext revitContext,
        ITransactionService transactionService,
        IFamilyLoadOptionsFactory loadOptionsFactory)
    {
        _revitContext = revitContext;
        _transactionService = transactionService;
        _loadOptionsFactory = loadOptionsFactory;
    }

    public IReadOnlyList<string> ExtractTypeNamesFromFile(string filePath)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
        {
            SmartConLogger.Info($"[TypeExtractor] Document is null, returning empty list");
            return Array.Empty<string>();
        }

        List<string>? typeNames = null;
        bool loadSucceeded = false;
        string? loadError = null;

        SmartConLogger.Info($"[TypeExtractor] Starting extraction from: {filePath}");

        try
        {
            _transactionService.RunAndRollback("Extract Types", d =>
            {
                var loadOptions = _loadOptionsFactory.CreateLoadOptions();
                if (loadOptions is not Autodesk.Revit.DB.IFamilyLoadOptions familyLoadOptions)
                {
                    SmartConLogger.Info("[TypeExtractor] LoadOptions is not IFamilyLoadOptions, skipping");
                    return;
                }

                SmartConLogger.Info($"[TypeExtractor] Calling LoadFamily for type extraction...");
                bool loaded = d.LoadFamily(filePath, familyLoadOptions, out var loadedFamily);

                SmartConLogger.Info($"[TypeExtractor] LoadFamily result: loaded={loaded}, family={(loadedFamily is null ? "null" : $"'{loadedFamily.Name}'")}");

                if (!loaded || loadedFamily is null)
                {
                    loadError = $"LoadFamily returned loaded={loaded}, family={(loadedFamily is null ? "null" : "not null")}";
                    SmartConLogger.Info($"[TypeExtractor] {loadError}");
                    return;
                }

                loadSucceeded = true;
                var symbolIds = loadedFamily.GetFamilySymbolIds();
                SmartConLogger.Info($"[TypeExtractor] Found {symbolIds.Count} symbol IDs");

                typeNames = symbolIds
                    .Select(id => d.GetElement(id))
                    .OfType<FamilySymbol>()
                    .Select(s => s.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                SmartConLogger.Info($"[TypeExtractor] Extracted {typeNames.Count} named types");
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[TypeExtractor] EXCEPTION during extraction: {ex.GetType().Name}: {ex.Message}");
            loadError = $"Exception: {ex.GetType().Name}: {ex.Message}";
        }

        if (typeNames == null || typeNames.Count == 0)
        {
            SmartConLogger.Info($"[TypeExtractor] No types found. LoadSucceeded={loadSucceeded}, Error={loadError ?? "none"}");
        }

        return typeNames ?? new List<string>();
    }
}
