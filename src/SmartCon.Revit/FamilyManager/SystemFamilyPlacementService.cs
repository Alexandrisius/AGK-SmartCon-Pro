using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyPlacementService : ISystemFamilyPlacementService
{
    private readonly IRevitUIContext _revitUIContext;
    private readonly IFamilyFileResolver _fileResolver;

    public SystemFamilyPlacementService(
        IRevitUIContext revitUIContext,
        IFamilyFileResolver fileResolver)
    {
        _revitUIContext = revitUIContext;
        _fileResolver = fileResolver;
    }

    public void LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion)
    {
        var uiApp = _revitUIContext.GetUIApplication();
        var activeDoc = _revitUIContext.GetUIDocument().Document;

        if (uiApp is null || activeDoc is null)
        {
            SmartConLogger.Freeze("[SystemFamilyPlacement] ABORT: uiApp or activeDoc is null");
            return;
        }

        var resolved = _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevitVersion).GetAwaiter().GetResult();

        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            SmartConLogger.Freeze("[SystemFamilyPlacement] ABORT: No file resolved");
            return;
        }

        Document? sourceDoc = null;
        try
        {
            sourceDoc = uiApp.Application.OpenDocumentFile(resolved.AbsolutePath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[SystemFamilyPlacement] OpenDocumentFile failed: {ex.Message}");
            return;
        }

        try
        {
            var sourceType = FindTypeByName(sourceDoc, typeName);
            if (sourceType is null)
            {
                SmartConLogger.Freeze($"[SystemFamilyPlacement] Type '{typeName}' not found in source doc");
                return;
            }

            var existingType = FindTypeByName(activeDoc, sourceType.Name, sourceType.Category?.Id);
            if (existingType is not null)
            {
                try { sourceDoc.Close(false); } catch { }
                sourceDoc = null;
                ActivatePlacement(uiApp, existingType);
                return;
            }

            using (var tx = new Transaction(activeDoc, "Copy system type"))
            {
                tx.Start();

                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                var failOpts = tx.GetFailureHandlingOptions();
                failOpts.SetFailuresPreprocessor(new SuppressCopyDuplicatesPreprocessor());
                tx.SetFailureHandlingOptions(failOpts);

                ElementTransformUtils.CopyElements(
                    sourceDoc, new List<ElementId> { sourceType.Id }, activeDoc, null, options);

                tx.Commit();
            }

            var copiedType = FindTypeByName(activeDoc, sourceType.Name, sourceType.Category?.Id);

            try { sourceDoc.Close(false); } catch { }
            sourceDoc = null;

            if (copiedType is not null)
            {
                ActivatePlacement(uiApp, copiedType);
            }
        }
        finally
        {
            try { sourceDoc?.Close(false); } catch { }
        }
    }

    private static void ActivatePlacement(UIApplication uiApp, ElementType elementType)
    {
        uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(elementType);
    }

    private static ElementType? FindTypeByName(Document doc, string name, ElementId? categoryId = null)
    {
        var collector = new FilteredElementCollector(doc).OfClass(typeof(ElementType));

        if (categoryId is not null && categoryId != ElementId.InvalidElementId)
        {
            try { collector = collector.OfCategoryId(categoryId); }
            catch { }
        }

        return collector.Cast<ElementType>()
            .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }

    private sealed class SuppressCopyDuplicatesPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var failures = failuresAccessor.GetFailureMessages();
            foreach (var f in failures)
            {
                if (f.GetFailureDefinitionId() == BuiltInFailures.CopyPasteFailures.CannotCopyDuplicates)
                    failuresAccessor.DeleteWarning(f);
            }
            return FailureProcessingResult.Continue;
        }
    }
}
