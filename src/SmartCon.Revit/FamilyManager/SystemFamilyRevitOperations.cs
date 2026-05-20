using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;
using SmartCon.Revit.Selection;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyRevitOperations : ISystemFamilyRevitOperations
{
    private readonly RevitContext _revitContext;

    public SystemFamilyRevitOperations(IRevitContext revitContext)
    {
        _revitContext = (RevitContext)revitContext;
    }

    public IReadOnlyList<SelectedPipeType> PickPipeTypes()
    {
        var uiApp = _revitContext.GetUIApplication();
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc.Document;

        IList<Reference> refs;
        try
        {
            refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new PipeSelectionFilter(),
                "Select pipe elements");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return [];
        }

        var pipeTypes = new Dictionary<string, SelectedPipeType>();
        foreach (var r in refs)
        {
            var elem = doc.GetElement(r);
            if (elem is null) continue;

            var typeId = elem.GetTypeId();
            if (typeId == ElementId.InvalidElementId) continue;

            var typeElem = doc.GetElement(typeId);
            if (typeElem is not PipeType pt) continue;

            if (!pipeTypes.ContainsKey(pt.UniqueId))
                pipeTypes[pt.UniqueId] = new SelectedPipeType(pt.UniqueId, pt.Name);
        }

        return pipeTypes.Values.ToList();
    }

    public CreateCleanProjectResult CreateCleanProjectWithTypes(IReadOnlyList<string> pipeTypeUniqueIds)
    {
        var uiApp = _revitContext.GetUIApplication();
        var doc = uiApp.ActiveUIDocument.Document;
        var app = uiApp.Application;

        var pipeTypeIds = new List<ElementId>();
        foreach (var uid in pipeTypeUniqueIds)
        {
            var elem = doc.GetElement(uid);
            if (elem is PipeType pt)
                pipeTypeIds.Add(pt.Id);
        }

        if (pipeTypeIds.Count == 0)
            return new CreateCleanProjectResult(false, null, "No PipeType elements found", 0);

        var allIdsToCopy = new List<ElementId>(pipeTypeIds);
        CollectPipeTypeDependencies(doc, pipeTypeIds, allIdsToCopy);

        SmartConLogger.Freeze($"[SystemFamilyRevitOps] Total elements to copy: {allIdsToCopy.Count}");

        Document newDoc;
        try
        {
            newDoc = app.NewProjectDocument(UnitSystem.Metric);
        }
        catch (Exception ex)
        {
            return new CreateCleanProjectResult(false, null, $"Failed to create project: {ex.Message}", 0);
        }

        try
        {
            int copiedCount = 0;
            using (var tx = new Transaction(newDoc, "Copy pipe types"))
            {
                tx.Start();

                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                var copiedIds = ElementTransformUtils.CopyElements(
                    doc, allIdsToCopy, newDoc, null, options);

                copiedCount = copiedIds.Count;
                tx.Commit();
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"SmartCon_SystemPipeTypes_{Guid.NewGuid():N}.rvt");
            newDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
            newDoc.Close(false);

            var fileSize = new FileInfo(tempPath).Length;
            SmartConLogger.Freeze($"[SystemFamilyRevitOps] Saved .rvt: {tempPath} ({fileSize / 1024} KB), copied {copiedCount} elements");

            return new CreateCleanProjectResult(true, tempPath, null, copiedCount);
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[SystemFamilyRevitOps] Failed: {ex.GetType().Name}: {ex.Message}");
            try { newDoc.Close(false); } catch { }
            return new CreateCleanProjectResult(false, null, ex.Message, 0);
        }
    }

    private static void CollectPipeTypeDependencies(Document doc, List<ElementId> pipeTypeIds, List<ElementId> allIds)
    {
        var dependencyIds = new HashSet<ElementId>();

        foreach (var typeId in pipeTypeIds)
        {
            if (doc.GetElement(typeId) is not PipeType pt) continue;

            try
            {
                var rpm = pt.RoutingPreferenceManager;
                if (rpm is null) continue;

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
                            dependencyIds.Add(partId);
                            var partElem = doc.GetElement(partId);
                            if (partElem is not null)
                            {
                                var depType = partElem.GetTypeId();
                                if (depType != ElementId.InvalidElementId)
                                    dependencyIds.Add(depType);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Freeze($"[SystemFamilyRevitOps] RoutingPreference error for {pt.Name}: {ex.Message}");
            }
        }

        allIds.AddRange(dependencyIds);
        SmartConLogger.Freeze($"[SystemFamilyRevitOps] Collected {dependencyIds.Count} dependency IDs");
    }

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
