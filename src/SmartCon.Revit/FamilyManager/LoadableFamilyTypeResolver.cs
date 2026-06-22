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
        var openSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var _scope = SmartConLogger.BeginScope("LoadableFamilyTypeResolver", ("RfaFileName", rfaFileName), ("CatalogItemId", catalogItemId));
            SmartConLogger.FreezeThreadPool("LoadableResolver.beforeOpen");
            SmartConLogger.Freeze($"LoadableResolver: Starting OpenDocumentFile for '{rfaFileName}'");
            familyDoc = app.OpenDocumentFile(rfaFilePath);
            openSw.Stop();
            SmartConLogger.Freeze($"LoadableResolver: OpenDocumentFile completed in {openSw.ElapsedMilliseconds}ms");
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
            {
                SmartConLogger.Warn("OpenDocumentFile did not return a family document [Action: Verify file is a valid Revit .rfa, or check Revit version compatibility]");
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
                SmartConLogger.Info("No types in family document (types.Size=0)");
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

            SmartConLogger.Info($"Resolved {result.Count} type(s)");
            return result;
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope("LoadableFamilyTypeResolver", ("RfaFileName", rfaFileName), ("Stage", "Resolve"));
            SmartConLogger.Warn($"Failed to resolve types: {ex.Message} [Action: Check Revit journal for detailed error, or restart Revit if COM object is corrupted]");
            return [];
        }
        finally
        {
            if (familyDoc != null)
            {
                try { familyDoc.Close(false); }
                catch (Exception ex)
                {
                    using var _scope = SmartConLogger.BeginScope("LoadableFamilyTypeResolver", ("RfaFileName", rfaFileName), ("Stage", "Close"));
                    SmartConLogger.Warn($"Close failed: {ex.Message} [Action: Safe to ignore — Revit will release the document on its own]");
                }
                // See RevitFamilyDataExtractionService — RevitAPI Document is a
                // managed RCW wrapper, not a real COM object. ReleaseComObject on
                // it throws ArgumentException and leaves a half-cleaned-up RCW that
                // the GC finalizer will mishandle, zombifying the WPF render thread
                // (REVIT-237190). Skip when IsComObject returns false; Close(false)
                // above is the real lifetime-end.
                try { Marshal.ReleaseComObject(familyDoc); }
                catch { }
            }
        }
    }
}
