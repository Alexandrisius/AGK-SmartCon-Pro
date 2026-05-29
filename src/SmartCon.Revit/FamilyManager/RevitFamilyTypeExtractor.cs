using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реализация IFamilyTypeExtractor через Revit API.
/// Открывает .rfa как отдельный family document и читает FamilyManager.Types.
/// Не использует LoadFamily — исключает генерацию фантомных типов проектом.
/// </summary>
public sealed class RevitFamilyTypeExtractor : IFamilyTypeExtractor
{
    private readonly IRevitContext _revitContext;

    public RevitFamilyTypeExtractor(IRevitContext revitContext)
    {
        _revitContext = revitContext;
    }

    public IReadOnlyList<string> ExtractTypeNamesFromFile(string filePath)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null) return Array.Empty<string>();

        var app = doc.Application;
        Document? familyDoc = null;

        try
        {
            familyDoc = app.OpenDocumentFile(filePath);
            if (!familyDoc.IsFamilyDocument)
                return Array.Empty<string>();

            var fm = familyDoc.FamilyManager;
            var typeNames = new List<string>();

            foreach (FamilyType familyType in fm.Types)
            {
                if (!string.IsNullOrWhiteSpace(familyType.Name))
                {
                    typeNames.Add(familyType.Name);
                }
            }

            return typeNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"ExtractTypeNamesFromFile failed for '{filePath}': {ex.Message}");
            return Array.Empty<string>();
        }
        finally
        {
            if (familyDoc != null)
            {
                try
                {
                    familyDoc.Close(false);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Failed to close family document '{filePath}': {ex.Message}");
                }
            }
        }
    }
}
