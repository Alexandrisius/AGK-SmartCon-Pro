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
        var rfaFileName = System.IO.Path.GetFileName(rfaFilePath);
        var app = _revitUIContext.GetUIApplication().Application;
        Document? familyDoc = null;
        try
        {
            SmartConLogger.FreezeThreadPool("LoadableResolver.beforeOpen");
            double openMs;
            using (var _openMs = SmartConLogger.Measure("LoadableResolver.OpenDocumentFile"))
            {
                SmartConLogger.Freeze($"LoadableResolver: Starting OpenDocumentFile for '{rfaFileName}'");
                familyDoc = app.OpenDocumentFile(rfaFilePath);
                openMs = _openMs.GetElapsedMilliseconds();
                SmartConLogger.Freeze($"LoadableResolver: OpenDocumentFile completed in {openMs}ms");
            }
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
            {
                SmartConLogger.Warn($"OpenDocumentFile did not return a family document for '{rfaFileName}' (catalogItemId={catalogItemId}) [Action: Verify file is a valid Revit .rfa, or check Revit version compatibility]");
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
                SmartConLogger.Info($"No types in family document '{rfaFileName}' (types.Size=0)");
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

            SmartConLogger.Info($"Resolved {result.Count} type(s) from '{rfaFileName}'");
            return result;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Failed to resolve types from '{rfaFileName}' (catalogItemId={catalogItemId}): {ex.Message} [Action: Check Revit journal for detailed error, or restart Revit if COM object is corrupted]");
            return [];
        }
        finally
        {
            if (familyDoc != null)
            {
                try { familyDoc.Close(false); }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"Close failed for '{rfaFileName}': {ex.Message} [Action: Safe to ignore — Revit will release the document on its own]");
                }
                try { Marshal.ReleaseComObject(familyDoc); }
                catch { }
            }
        }
    }
}
