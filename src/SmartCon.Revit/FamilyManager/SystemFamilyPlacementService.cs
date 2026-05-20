using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyPlacementService : ISystemFamilyPlacementService
{
    private readonly RevitContext _revitContext;
    private readonly IFamilyFileResolver _fileResolver;

    public SystemFamilyPlacementService(
        IRevitContext revitContext,
        IFamilyFileResolver fileResolver)
    {
        _revitContext = (RevitContext)revitContext;
        _fileResolver = fileResolver;
    }

    public void LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion)
    {
        SmartConLogger.Freeze($"[SystemFamilyPlacement] START: catalogItemId={catalogItemId}, typeName={typeName}, targetRevit={targetRevitVersion}");

        var uiApp = _revitContext.GetUIApplication();
        var activeDoc = _revitContext.GetDocument();

        if (uiApp is null || activeDoc is null)
        {
            SmartConLogger.Freeze("[SystemFamilyPlacement] ABORT: uiApp or activeDoc is null");
            return;
        }

        var resolved = SmartConLogger.FreezeTimer("SystemFamilyPlacement.ResolveFile", () =>
            _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevitVersion).GetAwaiter().GetResult());

        SmartConLogger.Freeze($"[SystemFamilyPlacement] Resolved path: '{resolved.AbsolutePath}'");

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
            var sourceType = FindTypeByName(sourceDoc, typeName, null);
            if (sourceType is null)
            {
                SmartConLogger.Freeze($"[SystemFamilyPlacement] Type '{typeName}' not found in source doc");
                return;
            }

            SmartConLogger.Freeze($"[SystemFamilyPlacement] Found source type '{sourceType.Name}' (Id={sourceType.Id})");

            var existingType = FindTypeByName(activeDoc, sourceType.Name, sourceType.Category?.Id);
            if (existingType is not null)
            {
                SmartConLogger.Freeze($"[SystemFamilyPlacement] Type '{sourceType.Name}' already exists, activating");
                sourceDoc.Close(false);
                sourceDoc = null;
                ActivatePlacement(uiApp, existingType);
                return;
            }

            var idsToCopy = new List<ElementId> { sourceType.Id };
            CollectDependencies(sourceDoc, sourceType, idsToCopy);

            SmartConLogger.Freeze($"[SystemFamilyPlacement] Copying {idsToCopy.Count} elements");

            ElementType? copiedType = null;

            using (var tx = new Transaction(activeDoc, "Copy system type"))
            {
                tx.Start();

                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                var failOpts = tx.GetFailureHandlingOptions();
                failOpts.SetFailuresPreprocessor(new SuppressCopyDuplicatesPreprocessor());
                tx.SetFailureHandlingOptions(failOpts);

                var copiedIds = ElementTransformUtils.CopyElements(
                    sourceDoc, idsToCopy, activeDoc, null, options);

                tx.Commit();

                SmartConLogger.Freeze($"[SystemFamilyPlacement] CopyElements returned {copiedIds.Count} elements");
            }

            copiedType = FindTypeByName(activeDoc, sourceType.Name, sourceType.Category?.Id);
            
            sourceDoc.Close(false);
            sourceDoc = null;

            if (copiedType is not null)
            {
                ActivatePlacement(uiApp, copiedType);
                SmartConLogger.Freeze($"[SystemFamilyPlacement] Activated placement for '{copiedType.Name}'");
            }
            else
            {
                SmartConLogger.Freeze($"[SystemFamilyPlacement] Could not find copied type '{sourceType.Name}' in active doc");
            }
        }
        finally
        {
            sourceDoc?.Close(false);
        }
    }

    private static void ActivatePlacement(UIApplication uiApp, ElementType elementType)
    {
        uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(elementType);
    }

    private static ElementType? FindTypeByName(Document doc, string name, ElementId? categoryId)
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

    private static void CollectDependencies(Document sourceDoc, ElementType type, List<ElementId> ids)
    {
        if (type is PipeType pt)
        {
            try
            {
                var rpm = pt.RoutingPreferenceManager;
                if (rpm is null) return;

                foreach (RoutingPreferenceRuleGroupType groupType in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
                {
                    var ruleCount = rpm.GetNumberOfRules(groupType);
                    for (int i = 0; i < ruleCount; i++)
                    {
                        var rule = rpm.GetRule(groupType, i);
                        if (rule is null) continue;

                        var partId = rule.MEPPartId;
                        if (partId != ElementId.InvalidElementId)
                        {
                            ids.Add(partId);
                            var partElem = sourceDoc.GetElement(partId);
                            if (partElem is not null)
                            {
                                var depTypeId = partElem.GetTypeId();
                                if (depTypeId != ElementId.InvalidElementId)
                                    ids.Add(depTypeId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Freeze($"[SystemFamilyPlacement] RoutingPreference error: {ex.Message}");
            }
        }
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
