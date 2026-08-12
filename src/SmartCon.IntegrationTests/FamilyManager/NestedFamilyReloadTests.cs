using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.IntegrationTests.Support;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// #209: contract tests for the nested reload semantics the
/// family-document stale-update branch relies on.
/// Depth-1: plain <c>Document.LoadFamily(path, IFamilyLoadOptions)</c>
/// with overwrite DOES replace an embedded nested definition (the pre-fix
/// <c>LoadFamilySymbol</c> preserve-types path was a silent no-op).
/// Depth-N (shared grandchild at any depth): the grandchild exists as ONE
/// hoisted definition in the host family document — a direct overwrite
/// reload into the host updates it, and the hoisted definition is what
/// reaches projects (proven 2026-08-10). An intermediate family's internal
/// EditFamily view keeps its own stale embedded copy no matter what load
/// variant is used (direct reload, temp SaveAs copy, push-back) —
/// documented, cosmetic, unreachable from projects.
/// </summary>
public sealed class NestedFamilyReloadTests : RevitApiTest
{
    private const string ChildName = "SmartConReloadChild";
    private const string AssemblyName = "SmartConReloadAssembly";
    private const string BoltName = "SmartConReloadBolt";

    private string? _tempDir;
    private string? _childPath;
    private Document? _parentDoc;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        var template = SampleFiles.FindFamilyTemplate(Application);
        if (template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the nested reload probe");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConReload_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");

        // Child v1: shared, one named type.
        var childDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(childDoc, "v1"))
        {
            tx.Start();
            childDoc.FamilyManager.NewType("TypeA");
            childDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            tx.Commit();
        }
        childDoc.SaveAs(_childPath, new SaveAsOptions { OverwriteExistingFile = true });
        childDoc.Close(false);

        _parentDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(_parentDoc, "Load child"))
        {
            tx.Start();
            if (!_parentDoc.LoadFamily(_childPath, out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            tx.Commit();
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        try
        {
            if (_parentDoc is not null && _parentDoc.IsValidObject)
            {
                _parentDoc.Close(false);
            }
        }
        catch
        {
        }

        try
        {
            if (_tempDir is not null && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Test]
    public async Task PokeNetZero_DocToDoc_FlipsStampAndMerges()
    {
        // #209 (2026-08-11) — the production recipe, machine-independent:
        // child v2 on disk has a REAL content diff (TypeB); the source doc
        // is poked NET-ZERO in memory (add + remove a scratch parameter,
        // two commits, never saved — the owner's manual proof that Revit's
        // changedness stamp is a dirty flag with no content history), then
        // pushed doc-to-doc. The merge must land TypeB in the host.
        // The unpoked doc-to-doc result is logged for documentation (on the
        // diverged-lineage nut it returns the existing Family without
        // merging — an early-out on the unflipped stamp).
        var childV2 = Application.OpenDocumentFile(_childPath!);
        using (var tx = new Transaction(childV2, "v2"))
        {
            tx.Start();
            childV2.FamilyManager.NewType("TypeB");
            tx.Commit();
        }
        childV2.Save();
        childV2.Close(false);

        var sourceDoc = Application.OpenDocumentFile(_childPath!);
        try
        {
            var unpoked = sourceDoc.LoadFamily(_parentDoc!, new OverwriteLoadOptions());
            SmartConLogger.Info(
                $"PokeNetZero control: UNPOKED doc-to-doc returned {(unpoked is null ? "null" : "Family")}, " +
                $"types=[{string.Join(",", SymbolNames(_parentDoc!, FindFamily(_parentDoc!, ChildName)!))}]");

            const string pokeName = "__SmartConPoke__";
            using (var tx = new Transaction(sourceDoc, "Poke add"))
            {
                tx.Start();
#if REVIT2022_OR_GREATER
                sourceDoc.FamilyManager.AddParameter(pokeName, GroupTypeId.General, SpecTypeId.String.Text, false);
#else
                sourceDoc.FamilyManager.AddParameter(pokeName, BuiltInParameterGroup.PG_GENERAL, ParameterType.Text, false);
#endif
                tx.Commit();
            }
            using (var tx = new Transaction(sourceDoc, "Poke remove"))
            {
                tx.Start();
                var scratch = sourceDoc.FamilyManager.GetParameters()
                    .FirstOrDefault(p => string.Equals(p.Definition.Name, pokeName, StringComparison.Ordinal));
                if (scratch is not null)
                {
                    sourceDoc.FamilyManager.RemoveParameter(scratch);
                }
                sourceDoc.Regenerate();
                tx.Commit();
            }

            // Doc-to-doc LoadFamily manages its own transaction — the
            // target document must not be modifiable at call time.
            var pushed = sourceDoc.LoadFamily(_parentDoc!, new OverwriteLoadOptions());
            SmartConLogger.Info($"PokeNetZero: poked doc-to-doc returned {(pushed is null ? "null" : "Family")}");

            var types = SymbolNames(_parentDoc!, FindFamily(_parentDoc!, ChildName)!);
            SmartConLogger.Info($"PokeNetZero: host child types after = [{string.Join(",", types)}]");
            await Assert.That(pushed).IsNotNull();
            await Assert.That(types).Contains("TypeB");
        }
        finally
        {
            sourceDoc.Close(false);
        }
    }

    [Test]
    public async Task LoadFamily_WithOverwrite_ReplacesNestedDefinition()
    {
        // Child v2: same file path, extra type — the definition differs.
        var childDoc = Application.OpenDocumentFile(_childPath!);
        using (var tx = new Transaction(childDoc, "v2"))
        {
            tx.Start();
            childDoc.FamilyManager.NewType("TypeB");
            tx.Commit();
        }
        childDoc.Save();
        childDoc.Close(false);

        var nestedBefore = FindFamily(_parentDoc!, ChildName);
        await Assert.That(nestedBefore).IsNotNull();
        await Assert.That(SymbolNames(_parentDoc!, nestedBefore!))
            .DoesNotContain("TypeB");

        using (var tx = new Transaction(_parentDoc!, "Reload nested"))
        {
            tx.Start();
            var ok = _parentDoc!.LoadFamily(_childPath!, new OverwriteLoadOptions(), out _);
            tx.Commit();
            SmartConLogger.Info($"Depth-1 nested reload: LoadFamily returned {ok}");
        }

        var nestedAfter = FindFamily(_parentDoc!, ChildName);
        await Assert.That(nestedAfter).IsNotNull();
        await Assert.That(SymbolNames(_parentDoc!, nestedAfter!))
            .Contains("TypeB");
    }

    [Test]
    public async Task LoadFamily_Depth2Grandchild_DirectReloadIntoTop_ReachesProject()
    {
        // #209 round-3 DECISIVE contract (probes 2026-08-10): a shared
        // grandchild (bolt inside a NON-SHARED assembly inside the edited
        // top family) cannot be updated inside the intermediate — but it
        // does not need to be. A direct overwrite reload of the grandchild
        // into the TOP family document updates the hoisted definition, and
        // that hoisted definition is what reaches a PROJECT when the top
        // family is loaded (asserted below). The intermediate's internal
        // EditFamily view keeps a stale embedded copy — documented in the
        // sibling contract tests, cosmetic, unreachable from projects.
        // Production consequence: RevitFamilyLoadService
        // .ReloadNestedInFamilyDocument performs the direct reload for ANY
        // nesting depth; no chain discovery, no cascade instruction.
        var template = SampleFiles.FindFamilyTemplate(Application)!;

        var boltPath = Path.Combine(_tempDir!, BoltName + "Push.rfa");
        var assemblyPath = Path.Combine(_tempDir!, AssemblyName + "Push.rfa");

        var boltDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(boltDoc, "bolt v1"))
        {
            tx.Start();
            boltDoc.FamilyManager.NewType("TypeA");
            boltDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            tx.Commit();
        }
        boltDoc.SaveAs(boltPath, new SaveAsOptions { OverwriteExistingFile = true });
        boltDoc.Close(false);

        var assemblyDoc0 = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(assemblyDoc0, "assembly"))
        {
            tx.Start();
            if (!assemblyDoc0.LoadFamily(boltPath, out _))
                throw new InvalidOperationException("LoadFamily(bolt) returned false");
            tx.Commit();
        }
        assemblyDoc0.SaveAs(assemblyPath, new SaveAsOptions { OverwriteExistingFile = true });
        assemblyDoc0.Close(false);

        var topDoc = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(topDoc, "Load assembly"))
            {
                tx.Start();
                if (!topDoc.LoadFamily(assemblyPath, out _))
                    throw new InvalidOperationException("LoadFamily(assembly) returned false");
                tx.Commit();
            }

            var boltV2 = Application.OpenDocumentFile(boltPath);
            using (var tx = new Transaction(boltV2, "bolt v2"))
            {
                tx.Start();
                boltV2.FamilyManager.NewType("TypeB");
                tx.Commit();
            }
            boltV2.Save();
            boltV2.Close(false);

            // Direct overwrite reload of the grandchild into the TOP
            // family document — the entire production recipe.
            var topBoltLoaded = false;
            using (var tx = new Transaction(topDoc, "Reload bolt in top"))
            {
                tx.Start();
                topBoltLoaded = topDoc.LoadFamily(boltPath, new OverwriteLoadOptions(), out _);
                tx.Commit();
            }
            var topBoltTypes = SymbolNames(topDoc, FindFamily(topDoc, BoltName + "Push")!);
            SmartConLogger.Info(
                $"Depth-2 contract: top bolt reload returned {topBoltLoaded}, top-level bolt = [{string.Join(", ", topBoltTypes)}]");
            await Assert.That(topBoltLoaded).IsTrue();
            await Assert.That(topBoltTypes).Contains("TypeB");

            // The decisive assertion: which bolt reaches a PROJECT.
            var projectDoc = SampleFiles.NewMepTemplateDocument(Application);
            if (projectDoc is null)
            {
                Skip.Test("Project templates (.rte) not found — cannot assert the project-side contract");
                return;
            }
            try
            {
                var topTempPath = Path.Combine(_tempDir!, "TopPush.rfa");
                topDoc.SaveAs(topTempPath, new SaveAsOptions { OverwriteExistingFile = true });
                using (var tx = new Transaction(projectDoc, "Load top into project"))
                {
                    tx.Start();
                    if (!projectDoc.LoadFamily(topTempPath, new OverwriteLoadOptions(), out _))
                        throw new InvalidOperationException("LoadFamily(top into project) returned false");
                    tx.Commit();
                }
                var projectBolt = FindFamily(projectDoc, BoltName + "Push");
                var projectBoltTypes = projectBolt is null
                    ? new List<string>()
                    : SymbolNames(projectDoc, projectBolt);
                SmartConLogger.Info(
                    $"Depth-2 contract: PROJECT bolt = [{string.Join(", ", projectBoltTypes)}]");
                await Assert.That(projectBoltTypes).Contains("TypeB");
            }
            finally
            {
                projectDoc.Close(false);
            }
        }
        finally
        {
            topDoc.Close(false);
        }
    }

    [Test]
    public async Task LoadFamily_Depth2ViaNonSharedIntermediate_DirectReloadDoesNotPropagate()
    {
        // #209 round-3 contract (probe 2026-08-10): even when the
        // intermediate is NON-SHARED, a direct overwrite reload of the
        // shared grandchild into the TOP family document updates only the
        // hoisted top-level definition — the intermediate keeps its own
        // embedded copy (EditFamily shows the old content). Sharedness of
        // the intermediate does NOT change the wall.
        var template = SampleFiles.FindFamilyTemplate(Application)!;

        var boltPath = Path.Combine(_tempDir!, BoltName + "NonShared.rfa");
        var assemblyPath = Path.Combine(_tempDir!, AssemblyName + "NonShared.rfa");

        var boltDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(boltDoc, "bolt v1"))
        {
            tx.Start();
            boltDoc.FamilyManager.NewType("TypeA");
            boltDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            tx.Commit();
        }
        boltDoc.SaveAs(boltPath, new SaveAsOptions { OverwriteExistingFile = true });
        boltDoc.Close(false);

        // Assembly stays NON-SHARED (no FAMILY_SHARED set).
        var assemblyDoc0 = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(assemblyDoc0, "assembly"))
        {
            tx.Start();
            if (!assemblyDoc0.LoadFamily(boltPath, out _))
                throw new InvalidOperationException("LoadFamily(bolt) returned false");
            tx.Commit();
        }
        assemblyDoc0.SaveAs(assemblyPath, new SaveAsOptions { OverwriteExistingFile = true });
        assemblyDoc0.Close(false);

        var topDoc = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(topDoc, "Load assembly"))
            {
                tx.Start();
                if (!topDoc.LoadFamily(assemblyPath, out _))
                    throw new InvalidOperationException("LoadFamily(assembly) returned false");
                tx.Commit();
            }

            // Sanity: the shared bolt is hoisted flat into the top document.
            var hoistedBolt = FindFamily(topDoc, BoltName + "NonShared");
            SmartConLogger.Info(
                $"Depth-2 non-shared probe: hoisted bolt in top doc = {(hoistedBolt is null ? "MISSING" : "present")}, " +
                $"assembly-embedded bolt = [{string.Join(", ", OpenAssemblyAndGetBolt(topDoc, AssemblyName + "NonShared", BoltName + "NonShared"))}]");

            // Bolt v2 on disk, then a direct overwrite load into the TOP doc.
            var boltV2 = Application.OpenDocumentFile(boltPath);
            using (var tx = new Transaction(boltV2, "bolt v2"))
            {
                tx.Start();
                boltV2.FamilyManager.NewType("TypeB");
                tx.Commit();
            }
            boltV2.Save();
            boltV2.Close(false);

            using (var tx = new Transaction(topDoc, "Reload bolt"))
            {
                tx.Start();
                var ok = topDoc.LoadFamily(boltPath, new OverwriteLoadOptions(), out _);
                tx.Commit();
                SmartConLogger.Info($"Depth-2 non-shared probe: direct bolt reload returned {ok}");
            }

            var embeddedAfter = OpenAssemblyAndGetBolt(topDoc, AssemblyName + "NonShared", BoltName + "NonShared");
            SmartConLogger.Info(
                $"Depth-2 non-shared contract: assembly-embedded bolt after direct reload = [{string.Join(", ", embeddedAfter)}]");
            await Assert.That(embeddedAfter).DoesNotContain("TypeB");
        }
        finally
        {
            topDoc.Close(false);
        }
    }

    [Test]
    public async Task LoadFamily_Depth2IntermediateEmbeddedCopy_StaysStale_Documented()
    {
        // #209 round-2 contract test (probes 2026-08-07): a bolt nested
        // inside an ASSEMBLY nested inside the edited parent (depth 2)
        // CANNOT be reloaded through any load variant — every one returns
        // a success-looking value while the embedded definition stays
        // unchanged, and no IFamilyLoadOptions callback fires:
        //   · doc.LoadFamily(path, options) into an unsaved EditFamily copy → false;
        //   · sourceDoc.LoadFamily(copy, options) → returns the EXISTING Family, unchanged;
        //   · copy SaveAs + reload-in-place → same no-op;
        //   · direct load into the TOP family document → "succeeds" but the
        //     intermediate's INTERNAL embedded copy is untouched (asserted
        //     below; the hoisted top-level definition IS updated — see
        //     LoadFamily_Depth2Grandchild_DirectReloadIntoTop_ReachesProject).
        // Production consequence (RevitFamilyLoadService
        // .ReloadNestedInFamilyDocument): the direct reload into the host
        // document is performed at ANY depth — the intermediate's stale
        // embedded copy is cosmetic and unreachable from projects, and the
        // StaleFamilyUpdater post-verify (which hashes the hoisted
        // definition) makes a lying marker impossible.
        var template = SampleFiles.FindFamilyTemplate(Application)!;

        var boltPath = Path.Combine(_tempDir!, BoltName + ".rfa");
        var assemblyPath = Path.Combine(_tempDir!, AssemblyName + ".rfa");

        var boltDoc = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(boltDoc, "bolt v1"))
        {
            tx.Start();
            boltDoc.FamilyManager.NewType("TypeA");
            boltDoc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            tx.Commit();
        }
        boltDoc.SaveAs(boltPath, new SaveAsOptions { OverwriteExistingFile = true });
        boltDoc.Close(false);

        var assemblyDoc0 = Application.NewFamilyDocument(template);
        using (var tx = new Transaction(assemblyDoc0, "assembly"))
        {
            tx.Start();
            if (!assemblyDoc0.LoadFamily(boltPath, out _))
                throw new InvalidOperationException("LoadFamily(bolt) returned false");
            assemblyDoc0.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
            tx.Commit();
        }
        assemblyDoc0.SaveAs(assemblyPath, new SaveAsOptions { OverwriteExistingFile = true });
        assemblyDoc0.Close(false);

        var topDoc = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(topDoc, "Load assembly"))
            {
                tx.Start();
                if (!topDoc.LoadFamily(assemblyPath, out _))
                    throw new InvalidOperationException("LoadFamily(assembly) returned false");
                tx.Commit();
            }

            // Bolt v2 on disk (extra type), then a direct doc-to-doc load
            // into the TOP family document — the strongest reload variant.
            var boltV2 = Application.OpenDocumentFile(boltPath);
            using (var tx = new Transaction(boltV2, "bolt v2"))
            {
                tx.Start();
                boltV2.FamilyManager.NewType("TypeB");
                tx.Commit();
            }
            boltV2.Save();
            var directResult = boltV2.LoadFamily(topDoc, new OverwriteLoadOptions());
            boltV2.Close(false);

            // The load "succeeds" (non-null Family) — yet the depth-2
            // definition inside the assembly is untouched.
            await Assert.That(directResult).IsNotNull();
            var assemblyEmbeddedBolt = OpenAssemblyAndGetBolt(topDoc, AssemblyName, BoltName);
            SmartConLogger.Info(
                $"Depth-2 API wall contract: assembly-embedded bolt after direct reload = [{string.Join(", ", assemblyEmbeddedBolt)}]");
            await Assert.That(assemblyEmbeddedBolt).DoesNotContain("TypeB");
        }
        finally
        {
            topDoc.Close(false);
        }
    }

    /// <summary>
    /// Opens the assembly's EditFamily copy from the top document and
    /// returns the symbol names of the bolt nested inside it — i.e. the
    /// DEPTH-2 definition, not the flat shared entry.
    /// </summary>
    private static List<string> OpenAssemblyAndGetBolt(Document topDoc, string assemblyName, string boltName)
    {
        var assemblyCopy = topDoc.EditFamily(FindFamily(topDoc, assemblyName)!);
        try
        {
            var bolt = FindFamily(assemblyCopy, boltName);
            return bolt is null ? new List<string>() : SymbolNames(assemblyCopy, bolt);
        }
        finally
        {
            assemblyCopy.Close(false);
        }
    }

    private static Family? FindFamily(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
    }

    private static List<string> SymbolNames(Document doc, Family family)
    {
        return family.GetFamilySymbolIds()
            .Select(id => (doc.GetElement(id) as FamilySymbol)?.Name ?? string.Empty)
            .ToList();
    }

    private sealed class OverwriteLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
