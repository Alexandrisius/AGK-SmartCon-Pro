using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyRevitOperations : ISystemFamilyRevitOperations
{
    private readonly IRevitUIContext _revitUIContext;

    public SystemFamilyRevitOperations(IRevitUIContext revitUIContext)
    {
        _revitUIContext = revitUIContext;
    }

    public IReadOnlyList<SelectedSystemType> PickSystemTypes()
    {
        var uidoc = _revitUIContext.GetUIDocument();
        var doc = uidoc.Document;

        IList<Reference> refs;
        try
        {
            refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new Selection.SystemFamilySelectionFilter(),
                "Select system family elements");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return [];
        }

        var types = new Dictionary<string, SelectedSystemType>();
        foreach (var r in refs)
        {
            var elem = doc.GetElement(r);
            if (elem is null) continue;

            var typeId = elem.GetTypeId();
            if (typeId == ElementId.InvalidElementId) continue;

            var typeElem = doc.GetElement(typeId);
            if (typeElem is null) continue;

            var categoryName = typeElem.Category?.Name ?? "Unknown";

            if (!types.ContainsKey(typeElem.UniqueId))
                types[typeElem.UniqueId] = new SelectedSystemType(
                    typeElem.UniqueId,
                    typeElem.Name,
                    categoryName);
        }

        return types.Values.ToList();
    }

    public CreateCleanProjectResult CreateCleanProjectWithTypes(IReadOnlyList<string> typeUniqueIds)
    {
        var uiApp = _revitUIContext.GetUIApplication();
        var doc = uiApp.ActiveUIDocument.Document;
        var app = uiApp.Application;

        var typeIds = new List<ElementId>();
        string? categoryName = null;
        foreach (var uid in typeUniqueIds)
        {
            var elem = doc.GetElement(uid);
            if (elem is not null)
            {
                typeIds.Add(elem.Id);
                categoryName ??= elem.Category?.Name;
            }
        }

        if (typeIds.Count == 0)
            return new CreateCleanProjectResult(false, null, "No type elements found", 0);

        Document? newDoc = null;
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
            int copiedCount;
            using (var tx = new Transaction(newDoc, "Copy system types"))
            {
                tx.Start();

                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                var copiedIds = ElementTransformUtils.CopyElements(
                    doc, typeIds, newDoc, null, options);

                copiedCount = copiedIds.Count;
                tx.Commit();
            }

            var safeName = SanitizeFileName(categoryName ?? "SystemFamily") + ".rvt";
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                "SmartCon",
                "SystemFamily",
                safeName);

            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

            newDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
            newDoc.Close(false);
            newDoc = null;

            return new CreateCleanProjectResult(true, tempPath, null, copiedCount, categoryName);
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[SystemFamilyRevitOps] Failed: {ex.GetType().Name}: {ex.Message}");
            try { newDoc?.Close(false); } catch { }
            return new CreateCleanProjectResult(false, null, ex.Message, 0);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
