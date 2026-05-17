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
        if (doc is null) return Array.Empty<string>();

        List<string>? typeNames = null;

        _transactionService.RunAndRollback("Extract Types", d =>
        {
            var loadOptions = _loadOptionsFactory.CreateLoadOptions();
            if (loadOptions is not Autodesk.Revit.DB.IFamilyLoadOptions familyLoadOptions)
                return;

            if (!d.LoadFamily(filePath, familyLoadOptions, out var loaded) || loaded is null)
                return;

            typeNames = loaded.GetFamilySymbolIds()
                .Select(id => d.GetElement(id))
                .OfType<FamilySymbol>()
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

        return typeNames ?? new List<string>();
    }
}
