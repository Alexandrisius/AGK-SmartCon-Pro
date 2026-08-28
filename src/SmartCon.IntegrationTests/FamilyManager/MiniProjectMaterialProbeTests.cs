using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;
using Electrical = Autodesk.Revit.DB.Electrical;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// TEMPORARY forensic probe for issue #254 (material duplication in staged
/// mini-projects). DELETE AFTER DIAGNOSIS. Dumps:
/// 1) the material inventory of the DEFAULT template (NewProjectDocument),
/// 2) the material/segment/type/family inventory of the stored v1 mini-project.
/// Report: C:\Users\klim9\AppData\Local\Temp\opencode\probe\mini-inv.txt
/// </summary>
public sealed class MiniProjectMaterialProbeTests : RevitApiTest
{
    private const string ReportPath = @"C:\Users\klim9\AppData\Local\Temp\opencode\probe\mini-inv.txt";
    private const string V1MiniPath = @"D:\Project\dotNET\00_Архив\Библиотеки семейств\Тест\files\4711a7ef881a439dab56ab5a2f9c09d7\v1\Трубы.rvt";
    private const string LiveProjectPath = @"C:\Users\klim9\Yandex.Disk\02_Work\#Projects\02_dotNet\01-Рабочая\00-Файлы\SmartCon\ТестовыйПример_SmartCom_2025.rvt";
    private const string KanTypeName = "BP_Нержавеющая сталь";

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FlexRoutingReality()
    {
        var sb = new StringBuilder();
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            DumpFlexType(sb, doc, "FlexPipe", typeof(FlexPipeType));
            DumpFlexType(sb, doc, "FlexDuct", typeof(FlexDuctType));
        }
        finally
        {
            doc.Close(false);
        }
        File.WriteAllText(ReportPath3, sb.ToString());
        await Assert.That(File.Exists(ReportPath3)).IsTrue();
    }

    private static void DumpFlexType(StringBuilder sb, Document doc, string label, Type classType)
    {
        var t = new FilteredElementCollector(doc).OfClass(classType).Cast<MEPCurveType>().FirstOrDefault();
        sb.AppendLine($"{label}|{(t is null ? "<none-in-template>" : t.Name)}");
        if (t is null) return;
        try
        {
            using var mgr = t.RoutingPreferenceManager;
            if (mgr is null)
            {
                sb.AppendLine("  manager=null");
                return;
            }
            foreach (RoutingPreferenceRuleGroupType g in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
            {
                int n;
                try { n = mgr.GetNumberOfRules(g); } catch (Exception ex) { sb.AppendLine($"  GROUP|{g}|<{ex.GetType().Name}>"); continue; }
                if (n == 0) continue;
                sb.AppendLine($"  GROUP|{g}|rules={n}");
                for (var i = 0; i < n; i++)
                {
                    try
                    {
                        var rule = mgr.GetRule(g, i);
                        var part = rule.MEPPartId is not null && rule.MEPPartId != ElementId.InvalidElementId
                            ? doc.GetElement(rule.MEPPartId) : null;
                        sb.AppendLine($"    RULE[{i}]|partClass={(part is null ? "<none>" : part.GetType().Name)}|partName={part?.Name ?? "-"}");
                    }
                    catch (Exception ex) { sb.AppendLine($"    RULE[{i}]|<{ex.Message}>"); }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  manager threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task DuctSegmentReality()
    {
        var sb = new StringBuilder();
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            // 1) every element whose runtime class name contains 'Segment'
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                var cn = e.GetType().Name;
                if (cn.Contains("Segment"))
                {
                    var seg = e as Segment;
                    sb.AppendLine($"SEGCLASS|{cn}|{e.Name}|matId={(seg is null ? "n/a" : seg.MaterialId?.ToString() ?? "null")}|id={e.Id}");
                }
            }
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var cn = e.GetType().Name;
                if (cn.Contains("Segment"))
                {
                    var seg = e as Segment;
                    sb.AppendLine($"SEGCLASS-NONTYPE|{cn}|{e.Name}|matId={(seg is null ? "n/a" : seg.MaterialId?.ToString() ?? "null")}|id={e.Id}");
                }
            }
            // 2) duct type routing manager — what groups exist and what the parts resolve to
            var ductType = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>().FirstOrDefault();
            sb.AppendLine($"DUCTTYPE|{(ductType is null ? "<none>" : ductType.Name)}");
            if (ductType is not null)
            {
                using var mgr = ductType.RoutingPreferenceManager;
                foreach (RoutingPreferenceRuleGroupType g in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
                {
                    int n;
                    try { n = mgr.GetNumberOfRules(g); } catch { continue; }
                    if (n == 0) continue;
                    sb.AppendLine($"  GROUP|{g}|rules={n}");
                    for (var i = 0; i < n; i++)
                    {
                        try
                        {
                            var rule = mgr.GetRule(g, i);
                            var part = rule.MEPPartId is not null && rule.MEPPartId != ElementId.InvalidElementId
                                ? doc.GetElement(rule.MEPPartId)
                                : null;
                            sb.AppendLine($"    RULE[{i}]|partId={rule.MEPPartId}|partClass={(part is null ? "<none>" : part.GetType().Name)}|partName={part?.Name ?? "-"}");
                        }
                        catch (Exception ex) { sb.AppendLine($"    RULE[{i}]|<{ex.Message}>"); }
                    }
                }
            }
            // 3) same for pipe type (control group)
            var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault();
            if (pipeType is not null)
            {
                using var mgr = pipeType.RoutingPreferenceManager;
                foreach (RoutingPreferenceRuleGroupType g in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
                {
                    int n;
                    try { n = mgr.GetNumberOfRules(g); } catch { continue; }
                    if (n == 0) continue;
                    sb.AppendLine($"  PIPE GROUP|{g}|rules={n}");
                    for (var i = 0; i < n; i++)
                    {
                        try
                        {
                            var rule = mgr.GetRule(g, i);
                            var part = rule.MEPPartId is not null && rule.MEPPartId != ElementId.InvalidElementId
                                ? doc.GetElement(rule.MEPPartId)
                                : null;
                            var mat = part is Segment s ? (s.MaterialId?.ToString() ?? "null") : "n/a";
                            sb.AppendLine($"    RULE[{i}]|partClass={(part is null ? "<none>" : part.GetType().Name)}|partName={part?.Name ?? "-"}|matId={mat}");
                        }
                        catch (Exception ex) { sb.AppendLine($"    RULE[{i}]|<{ex.Message}>"); }
                    }
                }
            }
        }
        finally
        {
            doc.Close(false);
        }
        File.WriteAllText(ReportPath3, sb.ToString());
        await Assert.That(File.Exists(ReportPath3)).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task DumpInventories()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== HOST: VersionName='{Application.VersionName}' VersionNumber='{Application.VersionNumber}' VersionBuild='{Application.VersionBuild}' Language='{Application.Language}'");
        try
        {
            sb.AppendLine($"DefaultProjectTemplate='{Application.DefaultProjectTemplate}'");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"DefaultProjectTemplate: <{ex.GetType().Name}: {ex.Message}>");
        }

        // --- 1) Default template inventory
        var templateDoc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            sb.AppendLine($"=== DEFAULT TEMPLATE (Title='{templateDoc.Title}', PathName='{templateDoc.PathName}')");
            var materials = new FilteredElementCollector(templateDoc).OfClass(typeof(Material)).Cast<Material>().ToList();
            sb.AppendLine($"materials: {materials.Count}");
            foreach (var m in materials.OrderBy(m => m.Name))
            {
                sb.AppendLine($"  MAT|{m.Name}");
            }
            sb.AppendLine($"  HAS_KAN={materials.Any(m => m.Name == "KAN-therm - Inox")} HAS_MED={materials.Any(m => m.Name == "Медь")}");
            var templateSegments = new FilteredElementCollector(templateDoc).OfClass(typeof(Segment)).Cast<Segment>().ToList();
            sb.AppendLine($"segments: {templateSegments.Count}");
            foreach (var s in templateSegments)
            {
                var mat = s.MaterialId != ElementId.InvalidElementId ? templateDoc.GetElement(s.MaterialId)?.Name : "<none>";
                sb.AppendLine($"  SEG|{s.Name}|mat={mat}");
            }
            var pipeTypes = new FilteredElementCollector(templateDoc).OfClass(typeof(PipeType)).Cast<PipeType>().ToList();
            sb.AppendLine($"pipe types: {pipeTypes.Count}");
            foreach (var pt in pipeTypes)
            {
                sb.AppendLine($"  PT|{pt.Name}");
            }
        }
        finally
        {
            templateDoc.Close(false);
        }

        // --- 2) v1 mini-project inventory
        var mini = Application.OpenDocumentFile(V1MiniPath);
        try
        {
            sb.AppendLine($"=== V1 MINI-PROJECT (Title='{mini.Title}')");
            var materials = new FilteredElementCollector(mini).OfClass(typeof(Material)).Cast<Material>().ToList();
            sb.AppendLine($"materials: {materials.Count}");
            foreach (var m in materials.OrderBy(m => m.Name))
            {
                sb.AppendLine($"  MAT|{m.Name}");
            }
            var segments = new FilteredElementCollector(mini).OfClass(typeof(Segment)).Cast<Segment>().ToList();
            sb.AppendLine($"segments: {segments.Count}");
            foreach (var s in segments)
            {
                var mat = s.MaterialId != ElementId.InvalidElementId ? mini.GetElement(s.MaterialId)?.Name : "<none>";
                sb.AppendLine($"  SEG|{s.Name}|mat={mat}");
            }
            var pipeTypes = new FilteredElementCollector(mini).OfClass(typeof(PipeType)).Cast<PipeType>().ToList();
            sb.AppendLine($"pipe types: {pipeTypes.Count}");
            foreach (var pt in pipeTypes)
            {
                var segParam = pt.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM);
                var segName = segParam is not null && segParam.AsElementId() != ElementId.InvalidElementId
                    ? mini.GetElement(segParam.AsElementId())?.Name
                    : "<none>";
                sb.AppendLine($"  PT|{pt.Name}|seg={segName}");
            }
            var families = new FilteredElementCollector(mini).OfClass(typeof(Family)).Cast<Family>().ToList();
            sb.AppendLine($"families: {families.Count}");
            foreach (var f in families.OrderBy(f => f.Name))
            {
                sb.AppendLine($"  FAM|{f.Name}");
            }
            // collision pairs: names 'X' and 'X1' both present
            var names = materials.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var n in names.OrderBy(x => x))
            {
                if (n.Length > 1 && char.IsDigit(n[^1]) && names.Contains(n[..^1]))
                {
                    sb.AppendLine($"  COLLISION|{n[..^1]} + {n}");
                }
            }
        }
        finally
        {
            mini.Close(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        File.WriteAllText(ReportPath, sb.ToString());
        await Assert.That(File.Exists(ReportPath)).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task DumpMaterialIdentities()
    {
        var sb = new StringBuilder();
        DumpDocIdentities(sb, @"C:\Users\klim9\Yandex.Disk\02_Work\#Projects\02_dotNet\01-Рабочая\00-Файлы\SmartCon\ТестовыйПример_SmartCom_2025.rvt", "LIVE PROJECT");
        DumpDocIdentities(sb, V1MiniPath, "V1 MINI");
        File.WriteAllText(ReportPath2, sb.ToString());
        await Assert.That(File.Exists(ReportPath2)).IsTrue();
    }

    private const string ReportPath2 = @"C:\Users\klim9\AppData\Local\Temp\opencode\probe\identities.txt";
    private const string ReportPath3 = @"C:\Users\klim9\AppData\Local\Temp\opencode\probe\steps.txt";

    /// <summary>
    /// THE definitive experiment for #254: replay the production staging
    /// sequence step by step (NewProjectDocument → collision resolver →
    /// CopyElements → place pipe → normalize) and dump the KAN/Inox
    /// material+segment state AFTER EACH STEP to catch the exact moment the
    /// duplicate material is born.
    /// </summary>
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task StagingStepByStep()
    {
        var sb = new StringBuilder();
        var live = Application.OpenDocumentFile(
            @"C:\Users\klim9\Yandex.Disk\02_Work\#Projects\02_dotNet\01-Рабочая\00-Файлы\SmartCon\ТестовыйПример_SmartCom_2025.rvt");
        Document? mini = null;
        try
        {
            var pipeType = new FilteredElementCollector(live).OfClass(typeof(PipeType)).Cast<PipeType>()
                .First(t => t.Name == "BP_Нержавеющая сталь");
            sb.AppendLine($"SOURCE TYPE: {pipeType.Name} id={pipeType.Id} uid={pipeType.UniqueId}");
            var srcSeg = pipeType.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM);
            if (srcSeg is not null && srcSeg.AsElementId() != ElementId.InvalidElementId)
            {
                var seg = live.GetElement(srcSeg.AsElementId());
                var mat = seg is not null ? live.GetElement((seg as Segment)?.MaterialId ?? ElementId.InvalidElementId) : null;
                sb.AppendLine($"SOURCE SEG: {seg?.Name} id={seg?.Id} uid={seg?.UniqueId} mat={mat?.Name} matUid={mat?.UniqueId}");
            }

            var tx = new SmartCon.Revit.Transactions.RevitTransactionService(
                new SmartCon.IntegrationTests.Support.StubRevitContext(live));

            // STEP 1: new project from default template
            mini = Application.NewProjectDocument(UnitSystem.Metric);
            DumpState(sb, mini, "1-NewProjectDocument");

            // STEP 2: template collision resolver (types only, as production)
            SmartCon.Revit.FamilyManager.TemplateCollisionResolver.RenameConflictingTemplateTypes(
                tx, mini, BuiltInCategory.OST_PipeCurves, new[] { pipeType.Name });
            DumpState(sb, mini, "2-TemplateCollisionResolver");

            // STEP 3: CopyElements (production options)
            ICollection<ElementId> copied = [];
            tx.RunInTransaction(mini, "Copy system types", doc =>
            {
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new ProbeSkipDupes());
                copied = ElementTransformUtils.CopyElements(live, new List<ElementId> { pipeType.Id }, doc, null, options);
            });
            sb.AppendLine($"COPIED: {copied.Count} type(s)");
            DumpState(sb, mini, "3-CopyElements");

            // STEP 4: place one pipe of the copied type (as PlaceInstancesOnGrid)
            var copiedType = copied.Count > 0 ? mini.GetElement(copied.First()) as ElementType : null;
            var level = new FilteredElementCollector(mini).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).First();
            var systemType = new FilteredElementCollector(live).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystemType))
                .First().Id;
            tx.RunInTransaction(mini, "Place pipe", doc =>
            {
                var sysType = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Plumbing.PipingSystemType))
                    .First().Id;
                Pipe.Create(doc, sysType, copiedType!.Id, level.Id, new XYZ(0, 0, 0), new XYZ(3, 0, 0));
            });
            DumpState(sb, mini, "4-PlacePipe");

            // STEP 5: normalize diameter (as NormalizeInstanceDimensions)
            tx.RunInTransaction(mini, "Normalize", doc =>
            {
                foreach (var p in new FilteredElementCollector(doc).OfClass(typeof(Pipe)).Cast<Pipe>())
                {
                    p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(0.3280839895);
                }
            });
            DumpState(sb, mini, "5-Normalize");

            // STEP 6: definitive reference map — WHO uses X (2967) and X1 (12330)
            DumpMaterialUsage(sb, mini, "6-UsageMap");

            // STEP 7: contrast — copy a pipe INSTANCE (like the user's manual UI copy)
            var livePipe = new FilteredElementCollector(live).OfClass(typeof(Pipe)).Cast<Pipe>()
                .FirstOrDefault(p => p.GetTypeId() == pipeType.Id);
            if (livePipe is not null)
            {
                var mini2 = Application.NewProjectDocument(UnitSystem.Metric);
                try
                {
                    tx.RunInTransaction(mini2, "Copy pipe instance", doc =>
                    {
                        ElementTransformUtils.CopyElements(live, new List<ElementId> { livePipe.Id }, doc, null, new CopyPasteOptions());
                    });
                    DumpState(sb, mini2, "7-InstanceCopy");
                }
                finally
                {
                    mini2.Close(false);
                }
            }
            else
            {
                sb.AppendLine("--- STEP 7 skipped: no placed pipe of that type in the live project");
            }
        }
        finally
        {
            live.Close(false);
            if (mini is not null) mini.Close(false);
        }
        File.WriteAllText(ReportPath3, sb.ToString());
        await Assert.That(File.Exists(ReportPath3)).IsTrue();
    }

    private sealed class ProbeSkipDupes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            => DuplicateTypeAction.UseDestinationTypes;
    }

    /// <summary>
    /// Maps every material with 'Inox' in the name to its exact referrers:
    /// project-level elements via GetMaterialIds(true), plus a per-family
    /// EditFamily scan for geometry/type-param references.
    /// </summary>
    private void DumpMaterialUsage(StringBuilder sb, Document doc, string step)
    {
        sb.AppendLine($"--- STEP {step}");
        var targets = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .Where(m => m.Name.Contains("Inox")).Select(m => m.Id).ToHashSet();
        var referrers = new Dictionary<long, List<string>>();
        void AddUsage(ElementId matId, string who)
        {
            if (!targets.Contains(matId)) return;
            if (!referrers.TryGetValue(matId.Value, out var list)) referrers[matId.Value] = list = new List<string>();
            if (list.Count < 12) list.Add(who);
        }
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            foreach (var mid in e.GetMaterialIds(true)) AddUsage(mid, $"INST|{e.GetType().Name}|{e.Name}|id={e.Id}");
        }
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsElementType())
        {
            foreach (var mid in e.GetMaterialIds(true)) AddUsage(mid, $"TYPE|{e.GetType().Name}|{e.Name}|id={e.Id}");
            if (e is ElementType et)
            {
                foreach (Parameter p in et.Parameters)
                {
                    if (p.StorageType == StorageType.ElementId && targets.Contains(p.AsElementId()))
                    {
                        AddUsage(p.AsElementId(), $"TPARAM|{et.Name}|{p.Definition?.Name}|id={et.Id}");
                    }
                }
            }
        }
        foreach (var fam in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .Where(f => f.Name.Contains("KAN") || f.Name.Contains("HILTI")))
        {
            var famDoc = doc.EditFamily(fam);
            try
            {
                foreach (var e in new FilteredElementCollector(famDoc).WhereElementIsNotElementType())
                {
                    foreach (var mid in e.GetMaterialIds(true)) AddUsage(mid, $"FAM:{fam.Name}|{e.GetType().Name}|{e.Name}|id={e.Id}");
                }
                var fm = famDoc.FamilyManager;
                foreach (FamilyParameter fp in fm.GetParameters())
                {
                    if (fp.StorageType != StorageType.ElementId) continue;
                    foreach (FamilyType t in fm.Types)
                    {
                        try
                        {
                            var v = t.AsElementId(fp);
                            if (v is not null && targets.Contains(v)) AddUsage(v, $"FAM:{fam.Name}|FamilyParam:{fp.Definition.Name}|type:{t.Name}");
                        }
                        catch { }
                    }
                }
            }
            finally
            {
                famDoc.Close(false);
            }
        }
        foreach (var m in targets)
        {
            var me = doc.GetElement(m);
            sb.AppendLine($"  USAGE|{me?.Name}|id={m}");
            if (referrers.TryGetValue(m.Value, out var list))
            {
                foreach (var r in list) sb.AppendLine($"    <- {r}");
            }
            else
            {
                sb.AppendLine("    <- <NO REFERRERS FOUND>");
            }
        }
    }

    private static void DumpState(StringBuilder sb, Document doc, string step)
    {
        sb.AppendLine($"--- STEP {step}");
        var mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .Where(m => m.Name.Contains("Inox") || m.Name.Contains("KAN") || m.Name.Contains("Stainless"))
            .ToList();
        foreach (var m in mats.OrderBy(m => m.Id.Value))
        {
            sb.AppendLine($"  MAT|id={m.Id}|{m.Name}");
        }
        foreach (var s in new FilteredElementCollector(doc).OfClass(typeof(Segment)).Cast<Segment>()
            .Where(s => s.Name.Contains("Inox") || s.Name.Contains("KAN") || s.Name.Contains("Stainless")))
        {
            var mat = doc.GetElement(s.MaterialId);
            sb.AppendLine($"  SEG|id={s.Id}|{s.Name}|matId={s.MaterialId}|mat={mat?.Name}");
        }
        foreach (var pt in new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>())
        {
            var sp = pt.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM);
            var segElem = sp is not null && sp.AsElementId() != ElementId.InvalidElementId ? doc.GetElement(sp.AsElementId()) : null;
            sb.AppendLine($"  PT|id={pt.Id}|{pt.Name}|seg={segElem?.Name ?? "<none>"}");
        }
    }

    private void DumpDocIdentities(StringBuilder sb, string path, string label)
    {
        var doc = Application.OpenDocumentFile(path);
        try
        {
            sb.AppendLine($"=== {label} (Title='{doc.Title}')");
            var mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .Where(m => m.Name.Contains("KAN-therm") || m.Name.Contains("Медь") || m.Name.StartsWith("BP_"))
                .ToList();
            foreach (var m in mats.OrderBy(m => m.Name))
            {
                sb.AppendLine($"  MAT|{m.Name}|id={m.Id}|uid={m.UniqueId}");
            }
            var seg = new FilteredElementCollector(doc).OfClass(typeof(Segment)).Cast<Segment>()
                .FirstOrDefault(s => s.Name.Contains("KAN-therm") || s.Name.Contains("52318"));
            if (seg is not null)
            {
                var mat = doc.GetElement(seg.MaterialId);
                sb.AppendLine($"  SEG|{seg.Name}|matId={seg.MaterialId}|matName={mat?.Name}|matUid={mat?.UniqueId}");
            }
            var fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => f.Name == "BP_A0302_KAN-therm_Inox");
            // Dependents of each colliding material: who would break if it were deleted
            foreach (var m in mats.Where(m => m.Name.Contains("KAN-therm - Inox")))
            {
                try
                {
                    var deps = m.GetDependentElements(new ElementCategoryFilter(ElementId.InvalidElementId, true));
                    sb.AppendLine($"  DEPS_OF|{m.Name}|id={m.Id}|count={deps.Count}");
                    foreach (var d in deps.Take(15))
                    {
                        var e = doc.GetElement(d);
                        sb.AppendLine($"    DEP|{(e is null ? d.ToString() : $"{e.GetType().Name}|{e.Name}|id={d}")}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  DEPS_OF|{m.Name}|id={m.Id}|<{ex.GetType().Name}: {ex.Message}>");
                }
            }
            if (fam is not null)
            {
                var famDoc = doc.EditFamily(fam);
                try
                {
                    var fmats = new FilteredElementCollector(famDoc).OfClass(typeof(Material)).Cast<Material>()
                        .Where(m => m.Name.Contains("KAN-therm") || m.Name.Contains("Inox")).ToList();
                    foreach (var m in fmats)
                    {
                        sb.AppendLine($"  FAMMAT|{m.Name}|id={m.Id}|uid={m.UniqueId}");
                    }
                    var fm = famDoc.FamilyManager;
                    var caseParam = fm.get_Parameter("BP_CaseMaterial");
                    foreach (FamilyType t in fm.Types)
                    {
                        var val = caseParam is not null ? t.AsElementId(caseParam) : null;
                        var matElem = val is not null && val != ElementId.InvalidElementId ? famDoc.GetElement(val) : null;
                        sb.AppendLine($"  FAMTYPE|{t.Name}|BP_CaseMaterial_id={val}|matName={matElem?.Name}|matUid={matElem?.UniqueId}");
                    }
                }
                finally
                {
                    famDoc.Close(false);
                }
            }
        }
        finally
        {
            doc.Close(false);
        }
    }

    // ── Phase 0 probes (ADR-072) — P0.1/P0.4/P0.5, P0.2, P0.3 ────────────

    /// <summary>
    /// P0.1 + P0.4 + P0.5: manual staging parity. Runs the PRODUCTION sync
    /// machinery live→fresh-mini (Duplicate prototype + WriteParameters +
    /// segment sync, fittings skipped via NullFittingDependencyResolver) —
    /// this is exactly the target manual-staging sequence of ADR-072 §2.1.
    /// Verifies: (a) no duplicate materials are born (the #254 bug class is
    /// structurally absent without CopyElements); (b) SEGMENTS extraction
    /// from the manual mini equals live (P0.5); (c) VALUES parity on the
    /// locale-invariant intersection (matched by BuiltInParameter id / GUID);
    /// (d) routing survives SaveAs/reopen (P0.4).
    /// </summary>
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task ManualStagingParity()
    {
        var sb = new StringBuilder();
        var live = Application.OpenDocumentFile(LiveProjectPath);
        Document? mini = null;
        var savePath = Path.Combine(Path.GetDirectoryName(ReportPath)!, "manual-mini.rvt");
        try
        {
            var extractor = new RevitFamilySnapshotExtractor();
            var liveType = new FilteredElementCollector(live).OfClass(typeof(PipeType)).Cast<PipeType>()
                .First(t => t.Name == KanTypeName);
            var liveSnapshot = extractor.ExtractSingleSystemType(live, liveType.Id);
            sb.AppendLine($"LIVE|type={liveType.Name}|values={liveSnapshot.Values.Count}|segments={liveSnapshot.Segments?.Count ?? -1}|routingRules={liveSnapshot.Routing?.Rules.Count ?? -1}");

            mini = Application.NewProjectDocument(UnitSystem.Metric);
            var tx = new RevitTransactionService(new StubRevitContext(mini));
            var materialSync = new RevitMaterialSyncService();
            var sync = new SystemTypeSyncService(
                tx, extractor, new RevitSystemTypeFinder(), new SystemClock(),
                materialSync, new RevitSegmentSyncService(materialSync), new ProbeNullFittingResolver(),
                new RevitCompoundStructureSyncService(materialSync));

            var result = sync.SyncTypeFromSource(
                live, mini, KanTypeName, "probe-item", "v1", int.Parse(Application.VersionNumber));
            sb.AppendLine($"SYNC|status={result.Status}|written={result.ParametersWritten}|skipped={result.ParametersSkipped}|notConverged={result.NotConvergedCount}");

            // (a) duplicate-material check — the core #254 assertion
            var kanMats = new FilteredElementCollector(mini).OfClass(typeof(Material)).Cast<Material>()
                .Where(m => m.Name.Contains("Inox") || m.Name.Contains("KAN")).OrderBy(m => m.Id.Value).ToList();
            foreach (var m in kanMats) sb.AppendLine($"  MAT|id={m.Id}|{m.Name}");
            var exactName = kanMats.Count(m => m.Name == "KAN-therm - Inox");
            var suffixed = kanMats.Count(m => m.Name != "KAN-therm - Inox");
            sb.AppendLine($"MATCHECK|exact={exactName}|suffixed={suffixed}");

            // (b) + (c) snapshot parity
            var miniType = new FilteredElementCollector(mini).OfClass(typeof(PipeType)).Cast<PipeType>()
                .First(t => t.Name == KanTypeName);
            var miniSnapshot = extractor.ExtractSingleSystemType(mini, miniType.Id);
            CompareSegments(sb, liveSnapshot, miniSnapshot);
            CompareValuesByIdentity(sb, live, liveType, mini, miniType);

            // slim-mini routing state: Segments=1, fitting groups empty
            using (var mgr = miniType.RoutingPreferenceManager)
            {
                foreach (RoutingPreferenceRuleGroupType g in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
                {
                    int n;
                    try { n = mgr.GetNumberOfRules(g); } catch { continue; }
                    if (n > 0) sb.AppendLine($"  MINI GROUP|{g}|rules={n}");
                }
            }

            // (d) SaveAs/reopen stability (P0.4)
            mini.SaveAs(savePath, new SaveAsOptions { OverwriteExistingFile = true });
            mini.Close(false);
            mini = Application.OpenDocumentFile(savePath);
            var reopenedType = new FilteredElementCollector(mini).OfClass(typeof(PipeType)).Cast<PipeType>()
                .First(t => t.Name == KanTypeName);
            var reopenedSnapshot = extractor.ExtractSingleSystemType(mini, reopenedType.Id);
            sb.AppendLine("AFTER SAVEAS/REOPEN:");
            CompareSegments(sb, liveSnapshot, reopenedSnapshot);
            using (var mgr = reopenedType.RoutingPreferenceManager)
            {
                var segRules = mgr.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
                sb.AppendLine($"  REOPENED Segments rules={segRules}");
                await Assert.That(segRules).IsEqualTo(1);
            }

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(exactName).IsEqualTo(1);
            await Assert.That(suffixed).IsEqualTo(0);
        }
        finally
        {
            live.Close(false);
            if (mini is not null) mini.Close(false);
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ReportPath)!, "manual-parity.txt"), sb.ToString());
        await Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(ReportPath)!, "manual-parity.txt"))).IsTrue();
    }

    private static void CompareSegments(StringBuilder sb, SystemTypeSnapshot live, SystemTypeSnapshot mini)
    {
        var liveTokens = (live.Segments ?? Array.Empty<SegmentSnapshot>()).Select(SegmentToken).OrderBy(t => t).ToList();
        var miniTokens = (mini.Segments ?? Array.Empty<SegmentSnapshot>()).Select(SegmentToken).OrderBy(t => t).ToList();
        sb.AppendLine($"SEGMENTS|live={liveTokens.Count}|mini={miniTokens.Count}|equal={liveTokens.SequenceEqual(miniTokens)}");
        foreach (var t in liveTokens.Except(miniTokens)) sb.AppendLine($"  LIVE-ONLY|{t}");
        foreach (var t in miniTokens.Except(liveTokens)) sb.AppendLine($"  MINI-ONLY|{t}");
    }

    private static string SegmentToken(SegmentSnapshot s)
        => $"{s.Name}|{s.MaterialName}|{s.ScheduleName}|{s.Roughness:R}|" +
           string.Join(",", s.Sizes.Select(z => $"{z.NominalDiameter:R}/{z.InnerDiameter:R}/{z.OuterDiameter:R}/{z.UsedInSizeLists}/{z.UsedInSizing}"));

    /// <summary>
    /// Locale-invariant VALUES parity: parameters matched by BuiltInParameter
    /// id (internal), shared GUID, or name fallback. Cross-locale host (RU live
    /// project vs EN template) makes name-based matching useless for built-ins.
    /// </summary>
    private static void CompareValuesByIdentity(StringBuilder sb, Document liveDoc, ElementType liveType, Document miniDoc, ElementType miniType)
    {
        var miniParams = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (Parameter p in miniType.Parameters)
        {
            miniParams[ParamIdentity(p)] = p;
        }
        var converged = 0; var readOnlySkipped = 0; var missing = 0; var mismatch = 0;
        foreach (Parameter lp in liveType.Parameters)
        {
            var key = ParamIdentity(lp);
            if (!miniParams.TryGetValue(key, out var mp))
            {
                missing++;
                sb.AppendLine($"  VAL-MISSING|{lp.Definition.Name}|{key}");
                continue;
            }
            if (lp.IsReadOnly || mp.IsReadOnly)
            {
                readOnlySkipped++;
                continue;
            }
            var lt = ValueToken(liveDoc, lp);
            var mt = ValueToken(miniDoc, mp);
            if (lt == mt)
            {
                converged++;
            }
            else
            {
                mismatch++;
                sb.AppendLine($"  VAL-MISMATCH|{lp.Definition.Name}|live={lt}|mini={mt}");
            }
        }
        sb.AppendLine($"VALUES|converged={converged}|readOnly={readOnlySkipped}|missingInMini={missing}|mismatch={mismatch}");
    }

    private static string ParamIdentity(Parameter p)
    {
        if (p.IsShared)
        {
            try { return "guid:" + p.GUID; } catch { /* fall through */ }
        }
        if (p.Definition is InternalDefinition id && id.BuiltInParameter != BuiltInParameter.INVALID)
        {
            return "bip:" + (long)id.BuiltInParameter;
        }
        return "name:" + p.Definition.Name;
    }

    private static string ValueToken(Document doc, Parameter p)
    {
        if (!p.HasValue) return "<novalue>";
        switch (p.StorageType)
        {
            case StorageType.Double: return "D:" + p.AsDouble().ToString("R");
            case StorageType.Integer: return "I:" + p.AsInteger();
            case StorageType.String: return "S:" + (p.AsString() ?? string.Empty);
            case StorageType.ElementId:
                var el = p.AsElementId();
                if (el == ElementId.InvalidElementId) return "E:<none>";
                return "E:" + (doc.GetElement(el)?.Name ?? "<unresolved:" + el + ">");
            default: return "?";
        }
    }

    /// <summary>
    /// P0.2: parameter audit on three axes (writability, presence, ElementId
    /// resolvability) for the live KAN pipe type + deprecated routing-parameter
    /// scan (RBS_CURVETYPE_DEFAULT_*, REVIT-76496) + conduit/tray/flex storage
    /// check (owner screenshots 2026-08-29: conduit/tray fitting rows live in
    /// the type properties — params or manager?).
    /// </summary>
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task ParamAuditReality()
    {
        var sb = new StringBuilder();
        var live = Application.OpenDocumentFile(LiveProjectPath);
        var template = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            // 1) full parameter dump of the live KAN pipe type
            var liveType = new FilteredElementCollector(live).OfClass(typeof(PipeType)).Cast<PipeType>()
                .First(t => t.Name == KanTypeName);
            var snapshot = new RevitFamilySnapshotExtractor().ExtractSingleSystemType(live, liveType.Id);
            var included = snapshot.Values.Select(v => v.ParameterName).ToHashSet(StringComparer.Ordinal);
            sb.AppendLine($"=== LIVE TYPE PARAMS ({KanTypeName}), hash-included marked with *");
            foreach (Parameter p in liveType.Parameters)
            {
                var mark = included.Contains(p.Definition.Name) ? "*" : " ";
                var bip = p.Definition is InternalDefinition id && id.BuiltInParameter != BuiltInParameter.INVALID
                    ? ((long)id.BuiltInParameter).ToString() : "-";
                var guid = p.IsShared ? p.GUID.ToString() : "-";
                var targetClass = string.Empty;
                if (p.StorageType == StorageType.ElementId && p.HasValue && p.AsElementId() != ElementId.InvalidElementId)
                {
                    var target = live.GetElement(p.AsElementId());
                    targetClass = target is null ? "<unresolved>" : target.GetType().Name + ":" + target.Name;
                }
                sb.AppendLine($"  {mark}|{p.Definition.Name}|bip={bip}|guid={guid}|{p.StorageType}|ro={p.IsReadOnly}|has={p.HasValue}|{targetClass}");
            }

            // 2) presence axis: template PipeType param identities vs live
            var templatePipe = new FilteredElementCollector(template).OfClass(typeof(PipeType)).Cast<PipeType>().First();
            var templateKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (Parameter p in templatePipe.Parameters) templateKeys.Add(ParamIdentity(p));
            var liveOnly = 0;
            foreach (Parameter p in liveType.Parameters)
            {
                if (!templateKeys.Contains(ParamIdentity(p)))
                {
                    liveOnly++;
                    sb.AppendLine($"  PRESENCE-LIVE-ONLY|{p.Definition.Name}|{ParamIdentity(p)}|has={p.HasValue}");
                }
            }
            sb.AppendLine($"PRESENCE|liveOnly={liveOnly}");

            // 3) deprecated routing-parameter scan (REVIT-76496: unused since 2013)
            sb.AppendLine("=== DEPRECATED RBS_*_DEFAULT_* SCAN");
            var deprecatedBips = Enum.GetValues(typeof(BuiltInParameter)).Cast<BuiltInParameter>()
                .Where(b =>
                {
                    var n = b.ToString();
                    return n.Contains("CURVETYPE_DEFAULT") || n.Contains("DEFAULT_TEE") || n.Contains("DEFAULT_CROSS")
                        || n.Contains("DEFAULT_ELBOW") || n.Contains("DEFAULT_TRANSITION") || n.Contains("DEFAULT_UNION")
                        || n.Contains("DEFAULT_CAP") || n.Contains("DEFAULT_FLANGE") || n.Contains("DEFAULT_MECHANICAL_JOINT")
                        || n.Contains("DEFAULT_TAP");
                })
                .ToList();
            sb.AppendLine($"enum members found: {deprecatedBips.Count}");
            var probeTypes = new List<ElementType> { liveType, templatePipe };
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(FlexPipeType)).Cast<ElementType>().Take(1)) probeTypes.Add(t);
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(FlexDuctType)).Cast<ElementType>().Take(1)) probeTypes.Add(t);
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(DuctType)).Cast<ElementType>().Take(1)) probeTypes.Add(t);
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(Electrical.ConduitType)).Cast<ElementType>().Take(1)) probeTypes.Add(t);
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(Electrical.CableTrayType)).Cast<ElementType>().Take(1)) probeTypes.Add(t);
            foreach (var pt in probeTypes)
            {
                var doc = pt.Document;
                sb.AppendLine($"  TYPE|{pt.GetType().Name}|{pt.Name}");
                foreach (var bip in deprecatedBips)
                {
                    try
                    {
                        var p = pt.get_Parameter(bip);
                        if (p is null) continue;
                        var token = ValueToken(doc, p);
                        sb.AppendLine($"    DEPR|{bip}|ro={p.IsReadOnly}|has={p.HasValue}|{token}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"    DEPR|{bip}|<{ex.GetType().Name}>");
                    }
                }
            }

            // 4) conduit/tray manager groups (screenshots: fitting rows in type props)
            sb.AppendLine("=== CONDUIT/TRAY/FLEX MANAGER GROUPS (template)");
            var mgrTypes = new List<MEPCurveType>();
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(Electrical.ConduitType)).Cast<MEPCurveType>().Take(2)) mgrTypes.Add(t);
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(Electrical.CableTrayType)).Cast<MEPCurveType>().Take(2)) mgrTypes.Add(t);
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(FlexPipeType)).Cast<MEPCurveType>().Take(1)) mgrTypes.Add(t);
            foreach (var t in new FilteredElementCollector(template).OfClass(typeof(FlexDuctType)).Cast<MEPCurveType>().Take(1)) mgrTypes.Add(t);
            foreach (var mt in mgrTypes)
            {
                sb.AppendLine($"  MGR|{mt.GetType().Name}|{mt.Name}");
                try
                {
                    using var mgr = mt.RoutingPreferenceManager;
                    if (mgr is null) { sb.AppendLine("    manager=null"); continue; }
                    foreach (RoutingPreferenceRuleGroupType g in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
                    {
                        int n;
                        try { n = mgr.GetNumberOfRules(g); } catch (Exception ex) { sb.AppendLine($"    GROUP|{g}|<{ex.GetType().Name}>"); continue; }
                        if (n == 0) continue;
                        sb.AppendLine($"    GROUP|{g}|rules={n}");
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"    manager threw: {ex.GetType().Name}");
                }
            }
        }
        finally
        {
            live.Close(false);
            template.Close(false);
        }
        var path = Path.Combine(Path.GetDirectoryName(ReportPath)!, "param-audit.txt");
        File.WriteAllText(path, sb.ToString());
        await Assert.That(File.Exists(path)).IsTrue();
    }

    /// <summary>
    /// P0.3: prototype availability in the DEFAULT template — every system
    /// family class that manual staging must support needs at least one
    /// template type to Duplicate from (D5: the last type is undeletable).
    /// Covers pipe/duct(Round/Rect/Oval)/flex/conduit/tray/wire/insulation/
    /// lining.
    /// </summary>
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PrototypeAvailability()
    {
        var sb = new StringBuilder();
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var categories = new[]
            {
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_FlexDuctCurves,
                BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_Wire, BuiltInCategory.OST_PipeInsulations,
                BuiltInCategory.OST_DuctInsulations, BuiltInCategory.OST_DuctLinings,
            };
            foreach (var cat in categories)
            {
                sb.AppendLine($"=== {cat}");
                var types = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElementType))
                    .OfCategory(cat)
                    .Cast<ElementType>()
                    .ToList();
                var byFamily = types.GroupBy(t => t.GetType().Name + "|" + t.FamilyName).OrderBy(g => g.Key);
                foreach (var g in byFamily)
                {
                    sb.AppendLine($"  {g.Key}|count={g.Count()}|first={g.First().Name}");
                }
                if (types.Count == 0) sb.AppendLine("  <EMPTY>");
            }
        }
        finally
        {
            doc.Close(false);
        }
        var path = Path.Combine(Path.GetDirectoryName(ReportPath)!, "prototypes.txt");
        File.WriteAllText(path, sb.ToString());
        await Assert.That(File.Exists(path)).IsTrue();
    }

    private sealed class ProbeNullFittingResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }

    /// <summary>
    /// Phase-0 follow-up (ADR-072, owner screenshots 2026-08-29): the definitive
    /// storage map for fitting-selection settings across ALL MEPCurve type
    /// classes. For pipe/duct the RoutingPreferenceManager is the store
    /// (params stale, REVIT-76496); for flex/conduit/tray the manager is NULL
    /// (probe-proven) and the type-properties rows are the store. This probe
    /// answers, per type class: which routing-ish built-in parameters exist
    /// via get_Parameter, which are VISIBLE in the .Parameters collection
    /// (→ hash-inclusion today), and their writability/value.
    /// </summary>
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task RoutingStorageReality()
    {
        var sb = new StringBuilder();
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var tokens = new[]
            {
                "BEND", "TEE", "CROSS", "ELBOW", "UNION", "TRANSITION", "TAKEOFF",
                "FLANGE", "CAP", "MECHJOINT", "JUNCTION", "PREFERRED", "MULTISHAPE", "OVAL",
            };
            var candidates = Enum.GetValues(typeof(BuiltInParameter)).Cast<BuiltInParameter>()
                .Where(b =>
                {
                    var n = b.ToString();
                    return n.StartsWith("RBS_", StringComparison.Ordinal)
                        && tokens.Any(t => n.Contains(t, StringComparison.Ordinal));
                })
                .OrderBy(b => b.ToString())
                .ToList();
            sb.AppendLine($"candidate bips: {candidates.Count}");

            var types = new List<ElementType>();
            CollectInto(types, doc, typeof(PipeType));
            CollectInto(types, doc, typeof(DuctType));
            CollectInto(types, doc, typeof(FlexPipeType));
            CollectInto(types, doc, typeof(FlexDuctType));
            CollectInto(types, doc, typeof(Electrical.ConduitType));
            CollectInto(types, doc, typeof(Electrical.CableTrayType));

            foreach (var t in types)
            {
                var visibleKeys = new HashSet<long>();
                foreach (Parameter p in t.Parameters)
                {
                    if (p.Definition is InternalDefinition id && id.BuiltInParameter != BuiltInParameter.INVALID)
                    {
                        visibleKeys.Add((long)id.BuiltInParameter);
                    }
                }
                string managerState;
                try
                {
                    using var mgr = ((MEPCurveType)t).RoutingPreferenceManager;
                    managerState = mgr is null ? "null" : "alive";
                }
                catch (Exception ex)
                {
                    managerState = "threw:" + ex.GetType().Name;
                }
                sb.AppendLine($"=== {t.GetType().Name}|{t.FamilyName}|{t.Name}|manager={managerState}");
                foreach (var bip in candidates)
                {
                    Parameter? p;
                    try { p = t.get_Parameter(bip); }
                    catch (Exception ex) { sb.AppendLine($"  {bip}|<threw {ex.GetType().Name}>"); continue; }
                    if (p is null) continue;
                    var visible = visibleKeys.Contains((long)bip) ? "VISIBLE" : "hidden";
                    sb.AppendLine($"  {bip}|{visible}|ro={p.IsReadOnly}|has={p.HasValue}|{ValueToken(doc, p)}");
                }
            }
        }
        finally
        {
            doc.Close(false);
        }
        var path = Path.Combine(Path.GetDirectoryName(ReportPath)!, "routing-storage.txt");
        File.WriteAllText(path, sb.ToString());
        await Assert.That(File.Exists(path)).IsTrue();
    }

    private static void CollectInto(List<ElementType> target, Document doc, Type classType)
    {
        foreach (var t in new FilteredElementCollector(doc).OfClass(classType).Cast<ElementType>())
        {
            target.Add(t);
        }
    }
}
