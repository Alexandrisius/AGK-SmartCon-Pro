using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core.Executors;

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

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FlexRoutingReality()
    {
        var sb = new StringBuilder();
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            DumpFlexType<FlexPipeType>(sb, doc, "FlexPipe");
            DumpFlexType<FlexDuctType>(sb, doc, "FlexDuct");
        }
        finally
        {
            doc.Close(false);
        }
        File.WriteAllText(ReportPath3, sb.ToString());
        await Assert.That(File.Exists(ReportPath3)).IsTrue();
    }

    private static void DumpFlexType<T>(StringBuilder sb, Document doc, string label) where T : MEPCurveType
    {
        var t = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault();
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
}
