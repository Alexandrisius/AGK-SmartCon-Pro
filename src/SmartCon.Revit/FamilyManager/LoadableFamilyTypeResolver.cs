using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

public sealed class LoadableFamilyTypeResolver : ILoadableFamilyTypeResolver
{
    private readonly IRevitUIContext _revitUIContext;

    public LoadableFamilyTypeResolver(IRevitUIContext revitUIContext)
    {
        _revitUIContext = revitUIContext;
    }

    public IReadOnlyList<FamilyTypeDescriptor> ResolveTypesFromRfa(
        string rfaFilePath,
        string catalogItemId,
        string? versionId = null,
        string? fileId = null)
    {
        var app = _revitUIContext.GetUIApplication().Application;
        Document? familyDoc = null;
        try
        {
            familyDoc = app.OpenDocumentFile(rfaFilePath);
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
            {
                SmartConLogger.Warn(
                    $"[LoadableFamilyTypeResolver] OpenDocumentFile did not return a family document for '{rfaFilePath}'");
                return [];
            }

            var fm = familyDoc.FamilyManager;
            var result = new List<FamilyTypeDescriptor>();

            var symbolsByName = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .ToLookup(s => s.Name, s => s, StringComparer.Ordinal);

            if (fm.Types.Size == 0)
            {
                SmartConLogger.Info(
                    $"[LoadableFamilyTypeResolver] No types in family document '{rfaFilePath}' (types.Size=0)");
                return result;
            }

            var sortOrder = 0;
            foreach (FamilyType familyType in fm.Types)
            {
                if (string.IsNullOrWhiteSpace(familyType.Name)) continue;

                var symbol = symbolsByName[familyType.Name].FirstOrDefault();

                result.Add(new FamilyTypeDescriptor(
                    Id: Guid.NewGuid().ToString(),
                    CatalogItemId: catalogItemId,
                    Name: familyType.Name,
                    SortOrder: sortOrder++,
                    VersionId: versionId,
                    FileId: fileId,
                    ExtractionRunId: null,
                    UniqueId: symbol?.UniqueId));
            }

            SmartConLogger.Info(
                $"[LoadableFamilyTypeResolver] Resolved {result.Count} type(s) from '{rfaFilePath}'");
            return result;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"[LoadableFamilyTypeResolver] Failed to resolve types from '{rfaFilePath}': {ex.Message}");
            return [];
        }
        finally
        {
            if (familyDoc != null)
            {
                try { familyDoc.Close(false); }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"[LoadableFamilyTypeResolver] Close failed for '{rfaFilePath}': {ex.Message}");
                }
                try { Marshal.ReleaseComObject(familyDoc); }
                catch { }
            }
        }
    }
}
