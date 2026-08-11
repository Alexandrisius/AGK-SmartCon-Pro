using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Implementation;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// #209 contract tests (manual-test failure 2026-08-11): on the REAL flange
/// pair library the stale-update reload of a nested family returned true
/// (OnFamilyFound fired, overwrite=True) yet the embedded content stayed
/// v1 — the post-reload verification hash equaled the stored v1 hash.
/// Synthetic contract tests (trivial families) pass, so these tests run
/// the exact production recipe on the exact production files.
/// COMPLETE WALL INVENTORY for the nut (every variant returns "success",
/// the definition stays v1): LoadFamily path into family doc (depth-1),
/// into the pair (depth-2 hoisted), doc-to-doc, wrapper via
/// OnSharedFamilyFound(source=Family), into a project, LoadFamilySymbol
/// per loaded type in a project, and even a v3 file with an added type.
/// The non-shared intermediate assembly (PPR-C0880-Болт+Гайка+Шайба)
/// cannot even be opened: EditFamily throws "Loaded Family Editing
/// failed" despite IsEditable=true (first-call and isolated, probes
/// 2026-08-11); no assembly file exists on disk — it lives only inside
/// the pair. Lookup tables are present and healthy in all copies (not
/// the cause). Matches the Autodesk-confirmed API/UI reload divergence
/// (Revit API forum 2026-04-30, Revit 2026.4). The flange (depth-1)
/// reloads — but Revit does not propagate parameter groups on overwrite,
/// so strict verification (groups are content) fails there too when the
/// version diff includes a regroup. Verification of the embedded content
/// is the only reliable arbiter of what actually landed.
/// </summary>
public sealed class RealLibraryNestedReloadReproTests : RevitApiTest
{
    private const string CatalogRoot = @"d:\Project\dotNET\00_Архив\Библиотеки семейств\Тест";

    // From the 2026-08-11 diagnostic dump (catalog.db rows + plain hashes).
    private const string NutName = "PPR-C0807-F-Гайка-ГОСТ_5915_70-PIEC-PL-0108-G3";
    private const string NutV1Hash = "C83F0288";
    private const string NutV2Hash = "8F85157E";
    private const string NutV2Relative = @"files\6432bb2e21ab454a98ac82b8c346f523\v2\" + NutName + ".rfa";
    private const string NutV1Relative = @"files\6432bb2e21ab454a98ac82b8c346f523\v1\" + NutName + ".rfa";

    private const string FlangeName = "PPR-E1401-N-Фланец-ПлоскийПриварной-ГОСТ_33259_2015-PIFT-PL-0104-G3";
    private const string FlangeV2Relative = @"files\9236045173fd4692a26bebe0929e752e\v2\" + FlangeName + ".rfa";

    private string? _tempDir;
    private Document? _pairDoc;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        if (!Directory.Exists(CatalogRoot))
        {
            Skip.Test($"Owner test library not found: {CatalogRoot}");
            return;
        }

        // The catalog files are Revit 2025 format — older Revit cannot open them.
        if (int.TryParse(Application.VersionNumber, out var revitYear) && revitYear < 2025)
        {
            Skip.Test("Catalog files require Revit 2025+ (file format)");
            return;
        }

        var pairFile = Directory
            .EnumerateFiles(CatalogRoot, "Фланцевая пара.rfa", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (pairFile is null || !File.Exists(Path.Combine(CatalogRoot, NutV2Relative)))
        {
            Skip.Test("Owner test library not found (flange pair or nut v2 file missing)");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConRepro_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var pairCopy = Path.Combine(_tempDir, "Фланцевая пара.rfa");
        File.Copy(pairFile, pairCopy);
        _pairDoc = Application.OpenDocumentFile(pairCopy);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        try
        {
            if (_pairDoc is not null && _pairDoc.IsValidObject)
            {
                _pairDoc.Close(false);
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
    public async Task ReloadNestedV2_IntoRealFlangePair_KeepsV1_ConfirmedApiWall()
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();

        string HashEmbedded()
        {
            var nested = new FilteredElementCollector(_pairDoc!)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .First(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
            var copy = _pairDoc!.EditFamily(nested);
            try
            {
                return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString;
            }
            finally
            {
                copy.Close(false);
            }
        }

        var beforeHash = HashEmbedded();
        SmartConLogger.Info($"RealRepro: embedded nut BEFORE reload = {beforeHash[..8]} (expect v1={NutV1Hash})");

        var v2Path = Path.Combine(CatalogRoot, NutV2Relative);
        var options = new RevitFamilyLoadOptions(
            overwriteParameterValues: true,
            onStatusMessage: null,
            onSharedDecision: null,
            nestedSharedNames: null);
        var loaded = false;
        using (var tx = new Transaction(_pairDoc!, "Reload nut v2"))
        {
            tx.Start();
            loaded = _pairDoc!.LoadFamily(v2Path, options, out _);
            tx.Commit();
        }
        SmartConLogger.Info($"RealRepro: LoadFamily(v2) returned {loaded}");

        var afterHash = HashEmbedded();
        SmartConLogger.Info(
            $"RealRepro: embedded nut AFTER reload = {afterHash[..8]} (v1={NutV1Hash}, v2={NutV2Hash})");

        // The arbiter: which nut reaches a PROJECT — the hoisted definition
        // (what actually ships) or the stale copy EditFamily shows?
        var projectDoc = SmartCon.IntegrationTests.Support.SampleFiles.NewMepTemplateDocument(Application);
        if (projectDoc is not null)
        {
            try
            {
                var pairSaved = Path.Combine(_tempDir!, "pair-saved.rfa");
                _pairDoc!.SaveAs(pairSaved, new SaveAsOptions { OverwriteExistingFile = true });
                using (var tx = new Transaction(projectDoc, "Load pair"))
                {
                    tx.Start();
                    projectDoc.LoadFamily(pairSaved, options, out _);
                    tx.Commit();
                }
                var projectNut = new FilteredElementCollector(projectDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
                if (projectNut is not null)
                {
                    var nutCopy = projectDoc.EditFamily(projectNut);
                    try
                    {
                        var projectHash = hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(nutCopy))!.HexString;
                        SmartConLogger.Info(
                            $"RealRepro: PROJECT nut = {projectHash[..8]} (v1={NutV1Hash}, v2={NutV2Hash})");
                    }
                    finally
                    {
                        nutCopy.Close(false);
                    }
                }
                else
                {
                    SmartConLogger.Info("RealRepro: nut not found in the project after loading the pair");
                }
            }
            finally
            {
                projectDoc.Close(false);
            }
        }

        // CONFIRMED WALL (probes 2026-08-11): this real-world family
        // (Pipe Accessories, shared, single space-named type, lookup-table
        // formulas) resists EVERY API reload variant — path-load,
        // doc-to-doc, wrapper via OnSharedFamilyFound, in family and
        // project documents, even with a type added to the file. Every
        // variant returns success (OnFamilyFound/OnSharedFamilyFound fire,
        // overwrite=True) while the embedded definition stays v1 — and the
        // pair loaded into a project ships the v1 nut too. Matches the
        // Autodesk-confirmed report that the LoadFamily API does not
        // replicate the manual UI reload (Revit API forum, 2026-04-30,
        // Revit 2026.4). The StaleFamilyUpdater post-verify is the arbiter:
        // it detects the no-op and keeps the family honestly stale. If a
        // future Revit version fixes the API, this test FAILS — that is
        // the signal to revisit the production flow.
        await Assert.That(loaded).IsTrue();
        await Assert.That(afterHash[..8]).IsEqualTo(NutV1Hash);
    }

    [Test]
    public async Task ReloadNutV2_IntoFreshFamilyDoc_IsolatesTheWall()
    {
        // Isolation ladder for the wall seen in the flange-pair repro:
        //   V1 — nut nested DIRECTLY (depth-1), no instance placed;
        //   V2 — same, but with a PLACED instance (FamilyInUse=True).
        // The real pair failed with the nut in use via the assembly; these
        // variants separate "family content resists reload" from "in use".
        var template = SmartCon.IntegrationTests.Support.SampleFiles.FindFamilyTemplate(Application)!;
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nutV1Path = Path.Combine(CatalogRoot, NutV1Relative);
        var nutV2Path = Path.Combine(CatalogRoot, NutV2Relative);

        string HashNut(Document doc)
        {
            var nested = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .First(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
            var copy = doc.EditFamily(nested);
            try
            {
                return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString;
            }
            finally
            {
                copy.Close(false);
            }
        }

        foreach (var placeInstance in new[] { false, true })
        {
            var host = Application.NewFamilyDocument(template);
            try
            {
                FamilySymbol? symbol = null;
                using (var tx = new Transaction(host, "Load nut v1"))
                {
                    tx.Start();
                    host.LoadFamily(nutV1Path, out _);
                    if (placeInstance)
                    {
                        var nested = new FilteredElementCollector(host)
                            .OfClass(typeof(Family))
                            .Cast<Family>()
                            .First(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
                        symbol = host.GetElement(nested.GetFamilySymbolIds().First()) as FamilySymbol;
                        if (symbol is not null && !symbol.IsActive) symbol.Activate();
                        host.FamilyCreate.NewFamilyInstance(XYZ.Zero, symbol!, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                    }
                    tx.Commit();
                }
                SmartConLogger.Info(
                    $"RealRepro V{(placeInstance ? 2 : 1)}: nut before = {HashNut(host)[..8]} (expect v1={NutV1Hash}), instance placed={placeInstance}, symbol={symbol?.Name ?? "<none>"}");

                var options = new RevitFamilyLoadOptions(true, null, null, null);
                var loaded = false;
                using (var tx = new Transaction(host, "Reload nut v2"))
                {
                    tx.Start();
                    loaded = host.LoadFamily(nutV2Path, options, out _);
                    tx.Commit();
                }
                SmartConLogger.Info(
                    $"RealRepro V{(placeInstance ? 2 : 1)}: LoadFamily(v2) returned {loaded}, nut after = {HashNut(host)[..8]} (v1={NutV1Hash}, v2={NutV2Hash})");
            }
            finally
            {
                host.Close(false);
            }
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ReloadNutV3WithNewType_IntoHostWithV1_DiscriminatesModifiedDetection()
    {
        // Discriminator for the nut wall: if a v3 (v2 + one extra type)
        // reload DOES replace the v1 definition in the same host where v2
        // silently failed, then Revit simply did not consider the v2 file
        // "modified" (file-level lineage/timestamp), not "the family
        // resists reloading". If even v3 fails, the family itself resists.
        var template = SmartCon.IntegrationTests.Support.SampleFiles.FindFamilyTemplate(Application)!;
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nutV1Path = Path.Combine(CatalogRoot, NutV1Relative);
        var nutV2Path = Path.Combine(CatalogRoot, NutV2Relative);

        // v3 = v2 + extra type (in a temp file).
        var nutV3Path = Path.Combine(_tempDir!, NutName + "_v3probe.rfa");
        var v3Doc = Application.OpenDocumentFile(nutV2Path);
        using (var tx = new Transaction(v3Doc, "v3 probe"))
        {
            tx.Start();
            v3Doc.FamilyManager.NewType("TypeProbeV3");
            tx.Commit();
        }
        v3Doc.SaveAs(nutV3Path, new SaveAsOptions { OverwriteExistingFile = true });
        v3Doc.Close(false);

        var host = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(host, "Load nut v1"))
            {
                tx.Start();
                host.LoadFamily(nutV1Path, out _);
                tx.Commit();
            }

            string[] SnapshotNut()
            {
                var nested = new FilteredElementCollector(host)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .First(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
                var copy = host.EditFamily(nested);
                try
                {
                    var snap = extractor.ExtractFromFamilyDocument(copy);
                    var hash = hasher.ComputeForLoadable(snap)!.HexString;
                    return new[] { hash[..8], string.Join(",", snap.Types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)) };
                }
                finally
                {
                    copy.Close(false);
                }
            }

            var before = SnapshotNut();
            SmartConLogger.Info($"RealRepro V3: before = {before[0]} types=[{before[1]}]");

            var options = new RevitFamilyLoadOptions(true, null, null, null);
            foreach (var (label, path) in new[] { ("v2", nutV2Path), ("v3+TypeProbe", nutV3Path) })
            {
                var loaded = false;
                using (var tx = new Transaction(host, $"Reload nut {label}"))
                {
                    tx.Start();
                    loaded = host.LoadFamily(path, options, out _);
                    tx.Commit();
                }
                var after = SnapshotNut();
                SmartConLogger.Info($"RealRepro V3: LoadFamily({label}) returned {loaded}, after = {after[0]} types=[{after[1]}]");
            }
        }
        finally
        {
            host.Close(false);
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ReloadNut_DocToDocOverload_AndSyntheticPhantom()
    {
        // Two probes in one run:
        //  A) doc-to-doc reload: v2doc.LoadFamily(host, options) — the
        //     Tammik-recommended reload pattern (#597), never tried against
        //     a normal host family document (only against EditFamily
        //     copies, where it is a proven no-op).
        //  B) synthetic PHANTOM-type family (no named types): does the
        //     "reload returns true but nothing changes" wall reproduce
        //     synthetically? (All passing contract families had NAMED
        //     types — the nut has none.)
        var template = SmartCon.IntegrationTests.Support.SampleFiles.FindFamilyTemplate(Application)!;
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nutV1Path = Path.Combine(CatalogRoot, NutV1Relative);
        var nutV2Path = Path.Combine(CatalogRoot, NutV2Relative);

        // A) doc-to-doc.
        var host = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(host, "Load nut v1"))
            {
                tx.Start();
                host.LoadFamily(nutV1Path, out _);
                tx.Commit();
            }
            string Hash()
            {
                var nested = new FilteredElementCollector(host)
                    .OfClass(typeof(Family)).Cast<Family>()
                    .First(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
                var copy = host.EditFamily(nested);
                try { return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString; }
                finally { copy.Close(false); }
            }

            var v2Doc = Application.OpenDocumentFile(nutV2Path);
            // Doc-to-doc LoadFamily manages its own transaction — the
            // target document must not be modifiable at call time.
            var result = v2Doc.LoadFamily(host, new RevitFamilyLoadOptions(true, null, null, null));
            v2Doc.Close(false);
            SmartConLogger.Info(
                $"RealRepro A: doc-to-doc LoadFamily returned {(result is null ? "null" : "Family")}, nut after = {Hash()[..8]} (v1={NutV1Hash}, v2={NutV2Hash})");
        }
        finally
        {
            host.Close(false);
        }

        // B) synthetic phantom-type child.
        var childPath = Path.Combine(_tempDir!, "PhantomChild.rfa");
        var child = Application.NewFamilyDocument(template);
        child.SaveAs(childPath, new SaveAsOptions { OverwriteExistingFile = true });
        child.Close(false);

        var hostB = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(hostB, "Load phantom v1"))
            {
                tx.Start();
                hostB.LoadFamily(childPath, out _);
                tx.Commit();
            }
            var typesBefore = PhantomTypes(hostB);

            // v2: add a family parameter (content change without named types).
            var childV2 = Application.OpenDocumentFile(childPath);
            using (var tx = new Transaction(childV2, "v2 param"))
            {
                tx.Start();
                childV2.FamilyManager.AddParameter(
                    "ProbeParam",
                    new ForgeTypeId("autodesk.parameter.group:general-1.0.0"),
                    new ForgeTypeId("autodesk.spec.aec:length-2.0.0"),
                    false);
                tx.Commit();
            }
            childV2.Save();
            childV2.Close(false);

            var loaded = false;
            using (var tx = new Transaction(hostB, "Reload phantom v2"))
            {
                tx.Start();
                loaded = hostB.LoadFamily(childPath, new RevitFamilyLoadOptions(true, null, null, null), out _);
                tx.Commit();
            }
            var typesAfter = PhantomTypes(hostB);
            var phantom = new FilteredElementCollector(hostB)
                .OfClass(typeof(Family)).Cast<Family>()
                .First(f => f.Name == "PhantomChild");
            var copy = hostB.EditFamily(phantom);
            bool hasProbeParam;
            try
            {
                hasProbeParam = copy.FamilyManager.GetParameters()
                    .Any(p => p.Definition.Name == "ProbeParam");
            }
            finally
            {
                copy.Close(false);
            }
            SmartConLogger.Info(
                $"RealRepro B: phantom reload returned {loaded}, typesBefore=[{typesBefore}] typesAfter=[{typesAfter}] ProbeParamPresent={hasProbeParam}");
        }
        finally
        {
            hostB.Close(false);
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task NutFileInternals_AndProjectReload()
    {
        // The nut file itself resists every family-document reload variant
        // while synthetic families (named types AND phantom) reload fine.
        // Inspect both files' internals (internal name, current type,
        // types count, shared flag, category) and try the PROJECT-document
        // reload path — does the nut reload there?
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nutV1Path = Path.Combine(CatalogRoot, NutV1Relative);
        var nutV2Path = Path.Combine(CatalogRoot, NutV2Relative);

        foreach (var (label, path) in new[] { ("v1", nutV1Path), ("v2", nutV2Path) })
        {
            var doc = Application.OpenDocumentFile(path);
            try
            {
                var fm = doc.FamilyManager;
                var owner = doc.OwnerFamily;
                var shared = owner?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger();
#if NET8_0_OR_GREATER
                var categoryId = owner?.FamilyCategory?.Id?.Value;
#else
                var categoryId = owner?.FamilyCategory?.Id?.IntegerValue;
#endif
                SmartConLogger.Info(
                    $"RealRepro C [{label}]: Title='{doc.Title}' OwnerFamily='{owner?.Name}' " +
                    $"Category='{owner?.FamilyCategory?.Name}'(id={categoryId}) shared={shared} " +
                    $"Types.Size={fm.Types.Size} CurrentType='{fm.CurrentType?.Name}' " +
                    $"params={fm.GetParameters().Count} hash={hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(doc))!.HexString[..8]}");
            }
            finally
            {
                doc.Close(false);
            }
        }

        // Project-document reload path.
        var project = SmartCon.IntegrationTests.Support.SampleFiles.NewMepTemplateDocument(Application);
        if (project is not null)
        {
            try
            {
                using (var tx = new Transaction(project, "Load nut v1"))
                {
                    tx.Start();
                    project.LoadFamily(nutV1Path, out _);
                    tx.Commit();
                }
                var loaded = false;
                using (var tx = new Transaction(project, "Reload nut v2"))
                {
                    tx.Start();
                    loaded = project.LoadFamily(nutV2Path, new RevitFamilyLoadOptions(true, null, null, null), out _);
                    tx.Commit();
                }
                var nut = new FilteredElementCollector(project)
                    .OfClass(typeof(Family)).Cast<Family>()
                    .First(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
                var copy = project.EditFamily(nut);
                string hash;
                try { hash = hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString; }
                finally { copy.Close(false); }
                SmartConLogger.Info(
                    $"RealRepro C [project]: reload returned {loaded}, nut in project = {hash[..8]} (v1={NutV1Hash}, v2={NutV2Hash})");
            }
            finally
            {
                project.Close(false);
            }
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ReloadNut_ViaWrapperFamily_OnSharedFamilyFoundPath()
    {
        // The direct reload fires OnFamilyFound (main family) and silently
        // keeps v1 — matching the confirmed Autodesk report that the
        // LoadFamily API does not replicate the manual reload (2026-04-30,
        // Revit 2026.4). The mechanism that DOES replace nested shared
        // definitions (in the UI and per the docs) is loading a CONTAINER
        // and answering the shared-family conflict with source=Family.
        // Probe: wrap the nut v2 in a scratch in-memory family and
        // doc-to-doc load the WRAPPER into the host — does the nut flip?
        var template = SmartCon.IntegrationTests.Support.SampleFiles.FindFamilyTemplate(Application)!;
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nutV1Path = Path.Combine(CatalogRoot, NutV1Relative);
        var nutV2Path = Path.Combine(CatalogRoot, NutV2Relative);

        string HashNut(Document doc)
        {
            var nested = new FilteredElementCollector(doc)
                .OfClass(typeof(Family)).Cast<Family>()
                .First(f => string.Equals(f.Name, NutName, StringComparison.OrdinalIgnoreCase));
            var copy = doc.EditFamily(nested);
            try { return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString; }
            finally { copy.Close(false); }
        }

        var host = Application.NewFamilyDocument(template);
        var wrapper = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(host, "Load nut v1"))
            {
                tx.Start();
                host.LoadFamily(nutV1Path, out _);
                tx.Commit();
            }
            using (var tx = new Transaction(wrapper, "Wrap nut v2"))
            {
                tx.Start();
                wrapper.LoadFamily(nutV2Path, out _);
                tx.Commit();
            }
            SmartConLogger.Info($"RealRepro D: before wrapper load, nut = {HashNut(host)[..8]} (v1={NutV1Hash})");

            // Doc-to-doc: the wrapper carries nut v2 as its shared nested;
            // the load must raise OnSharedFamilyFound for the nut →
            // source=Family (RevitFamilyLoadOptions default branch).
            var result = wrapper.LoadFamily(host, new RevitFamilyLoadOptions(true, null, null, null));
            SmartConLogger.Info(
                $"RealRepro D: wrapper doc-to-doc returned {(result is null ? "null" : "Family")}, nut after = {HashNut(host)[..8]} (v1={NutV1Hash}, v2={NutV2Hash})");
        }
        finally
        {
            wrapper.Close(false);
            host.Close(false);
        }

        await Task.CompletedTask;
    }

    private static string PhantomTypes(Document host)
    {
        var nested = new FilteredElementCollector(host)
            .OfClass(typeof(Family)).Cast<Family>()
            .FirstOrDefault(f => f.Name == "PhantomChild");
        if (nested is null) return "<none>";
        return string.Join(",", nested.GetFamilySymbolIds()
            .Select(id => (host.GetElement(id) as FamilySymbol)?.Name ?? "?"));
    }

    [Test]
    public async Task VerificationHash_Strict_BothMismatch_Documented()
    {
        // #209 round-5 contract (owner directive: STRICT, no cosmetic
        // tolerance). The verification-grade hash excludes only physically
        // host-dependent metrics (driven geometry, connector size/origin)
        // and is strict on definitions — INCLUDING parameter groups.
        // On the real library:
        //   · flange (depth-1): the reload DOES land v2's params/formulas,
        //     but Revit does NOT propagate parameter groups on overwrite —
        //     embedded keeps v1's groups → strict MISMATCH. An API update
        //     of a family whose version diff includes a regroup cannot be
        //     verified as complete — honest failure, manual UI reload is
        //     the remediation.
        //   · nut (depth-2): the reload silently no-ops (proven wall —
        //     see class doc) → embedded stays v1 → strict MISMATCH.
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();

        string EmbeddedVerifyHash(string familyName)
        {
            var nested = new FilteredElementCollector(_pairDoc!)
                .OfClass(typeof(Family)).Cast<Family>()
                .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
            var copy = _pairDoc!.EditFamily(nested);
            try { return hasher.ComputeForEmbeddedVerification(extractor.ExtractFromFamilyDocument(copy))!.HexString; }
            finally { copy.Close(false); }
        }

        string FileVerifyHash(string relativePath)
        {
            var doc = Application.OpenDocumentFile(Path.Combine(CatalogRoot, relativePath));
            try { return hasher.ComputeForEmbeddedVerification(extractor.ExtractFromFamilyDocument(doc))!.HexString; }
            finally { doc.Close(false); }
        }

        var options = new RevitFamilyLoadOptions(true, null, null, null);

        using (var tx = new Transaction(_pairDoc!, "Reload flange v2"))
        {
            tx.Start();
            _pairDoc!.LoadFamily(Path.Combine(CatalogRoot, FlangeV2Relative), options, out _);
            tx.Commit();
        }
        var flangeEmbedded = EmbeddedVerifyHash(FlangeName);
        var flangeFile = FileVerifyHash(FlangeV2Relative);
        SmartConLogger.Info(
            $"RealRepro V-hash STRICT: flange embedded={flangeEmbedded[..8]} file={flangeFile[..8]} equal={flangeEmbedded == flangeFile}");
        await Assert.That(flangeEmbedded).IsNotEqualTo(flangeFile);

        using (var tx = new Transaction(_pairDoc!, "Reload nut v2"))
        {
            tx.Start();
            _pairDoc!.LoadFamily(Path.Combine(CatalogRoot, NutV2Relative), options, out _);
            tx.Commit();
        }
        var nutEmbedded = EmbeddedVerifyHash(NutName);
        var nutFile = FileVerifyHash(NutV2Relative);
        SmartConLogger.Info(
            $"RealRepro V-hash STRICT: nut embedded={nutEmbedded[..8]} file={nutFile[..8]} equal={nutEmbedded == nutFile}");
        await Assert.That(nutEmbedded).IsNotEqualTo(nutFile);
    }
}
