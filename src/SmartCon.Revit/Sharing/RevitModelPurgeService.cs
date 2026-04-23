using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Sharing;

public sealed class RevitModelPurgeService : IModelPurgeService
{
    private readonly ITransactionService _transactionService;

    public RevitModelPurgeService(ITransactionService transactionService)
    {
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    }

    public int Purge(Document doc, PurgeOptions options, IReadOnlyList<string> keepViewNames)
    {
#if NETFRAMEWORK
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (keepViewNames is null) throw new ArgumentNullException(nameof(keepViewNames));
#else
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keepViewNames);
#endif
        var totalDeleted = 0;

        _transactionService.RunInTransaction("ShareProject: Purge", purgeDoc =>
        {
            if (options.PurgeGroups)
            {
                foreach (var g in new FilteredElementCollector(purgeDoc)
                             .OfClass(typeof(Group)).Cast<Group>())
                {
                    try { g.UngroupMembers(); } catch { }
                }
            }

            if (options.PurgeAssemblies)
            {
                foreach (var a in new FilteredElementCollector(purgeDoc)
                             .OfClass(typeof(AssemblyInstance)).Cast<AssemblyInstance>())
                {
                    try { a.Disassemble(); } catch { }
                }
            }

            var idsToDelete = new List<ElementId>();

            if (options.PurgeGroups)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(GroupType))
                    .Select(g => g.Id));
            }

            if (options.PurgeSpaces)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfCategory(BuiltInCategory.OST_MEPSpaces)
                    .Select(s => s.Id));
            }

            var viewports = new FilteredElementCollector(purgeDoc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .Select(vp => vp.ViewId)
                .ToHashSet();

            var viewsToDelete = new FilteredElementCollector(purgeDoc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Where(v => !keepViewNames.Contains(v.Name))
                .Where(v => !viewports.Contains(v.Id))
                .Select(v => v.Id)
                .ToList();
            idsToDelete.AddRange(viewsToDelete);

            if (options.PurgeRvtLinks)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Select(r => r.Id));
            }

            if (options.PurgeCadImports)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(ImportInstance))
                    .Select(i => i.Id));
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(CADLinkType))
                    .Select(t => t.Id));
            }

            if (options.PurgePointClouds)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(PointCloudInstance))
                    .Select(p => p.Id));
            }

            if (options.PurgeRebar)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfCategory(BuiltInCategory.OST_Rebar)
                    .Select(r => r.Id));
            }

            if (options.PurgeFabricReinforcement)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfCategory(BuiltInCategory.OST_FabricReinforcement)
                    .Select(f => f.Id));
            }

            if (options.PurgeImages)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(ImageInstance))
                    .Select(i => i.Id));
            }

            if (options.PurgeRvtLinks)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(RevitLinkType))
                    .Select(t => t.Id));
            }

            if (options.PurgeSheets)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(ViewSheet))
                    .Select(s => s.Id));
            }

            if (options.PurgeSchedules)
            {
                idsToDelete.AddRange(new FilteredElementCollector(purgeDoc)
                    .OfClass(typeof(ViewSchedule))
                    .Select(s => s.Id));
            }

            totalDeleted += SafeDelete(purgeDoc, idsToDelete);

            if (options.PurgeUnused)
            {
                totalDeleted += PurgeUnusedElements(purgeDoc);
            }
        });

        return totalDeleted;
    }

    private static int SafeDelete(Document doc, ICollection<ElementId> ids)
    {
        if (ids.Count == 0) return 0;

        try
        {
            doc.Delete(ids);
            return ids.Count;
        }
        catch
        {
            var deleted = 0;
            foreach (var id in ids)
            {
                try { doc.Delete(id); deleted++; } catch { }
            }
            return deleted;
        }
    }

    private static int PurgeUnusedElements(Document doc)
    {
        const string purgeRuleGuid = "e8c63650-70b7-435a-9010-ec97660c1bda";

        var ruleIds = PerformanceAdviser.GetPerformanceAdviser().GetAllRuleIds();
        var ruleId = ruleIds.FirstOrDefault(id => id.Guid.ToString() == purgeRuleGuid);
        if (ruleId is null) return 0;

        var totalDeleted = 0;
        while (true)
        {
            var messages = PerformanceAdviser.GetPerformanceAdviser()
                .ExecuteRules(doc, [ruleId]);

            if (messages.Count == 0) break;

            var elementIds = messages[0].GetFailingElements().ToList();
            if (elementIds.Count == 0) break;

            try { doc.Delete(elementIds); totalDeleted += elementIds.Count; }
            catch { break; }
        }

        return totalDeleted;
    }
}
