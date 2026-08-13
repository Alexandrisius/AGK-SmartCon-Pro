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
/// reloads — but Revit does not propagate parameter groups on overwrite.
/// Verification of the embedded content is the only reliable arbiter of
/// what actually landed.
///
/// ADDENDUM 2026-08-11 (poke breakthrough): the wall is BROKEN. Revit's
/// changedness check is a dirty flag, not a content diff — a net-zero
/// in-memory poke of the SOURCE document (add + remove a scratch family
/// parameter, two commits, never saved) flips it, and doc-to-doc
/// LoadFamily then takes the full merge path even through the API.
/// Groups never propagate on ANY merge (API or UI), so FHV10 does not
/// hash them. Production mechanism + contracts:
/// <c>NestedReloadPokeContractTests</c>. The tests below remain as
/// documentation of the RAW unpoked API behavior.
///
/// ADDENDUM 2026-08-12 (fixture drift): the owner's update-flow manual
/// test rewrote the v2 nut file in the library (and added a v3), so
/// v1↔v2 no longer differ ONLY by the parameter group — and with a
/// substantively-differing incoming file even the RAW unpoked reload
/// LANDS v2. The 2026-08-11 no-op wall was therefore specific to
/// group-only-diff content, not to this family in general. The nut
/// hashes are now computed live from the files in Seed, and the former
/// wall test asserts a DECISIVE outcome (landed v2 or stayed v1 — never
/// a silent partial merge).
/// </summary>
public sealed class RealLibraryNestedReloadReproTests : RevitApiTest
{
    private static string CatalogRoot =>
        Environment.GetEnvironmentVariable("SMARTCON_TEST_CATALOG")
        is { Length: > 0 } overridePath
            ? overridePath
            : @"d:\Project\dotNET\00_Архив\Библиотеки семейств\Тест";

    // FHV10 pins (2026-08-12, probe-computed from the owner library files).
    private const string NutName = "PPR-C0807-F-Гайка-ГОСТ_5915_70-PIEC-PL-0108-G3";

    // FHV10 hashes of the v1/v2 nut FILES — computed live in Seed. The
    // former const pins (_nutV1FileHash = _nutV2FileHash = 6846AC6F, 2026-08-12) went
    // stale the same day: the owner's update-flow manual test rewrote the
    // v2 file (and added a v3) — the owner library is a living fixture, so
    // the pins must follow the files, not freeze them.
    private string? _nutV1FileHash;
    private string? _nutV2FileHash;

    private const string FlangeName = "PPR-E1401-N-Фланец-ПлоскийПриварной-ГОСТ_33259_2015-PIFT-PL-0104-G3";

    private string? _tempDir;
    private Document? _pairDoc;
    private string? _nutV1Path;
    private string? _nutV2Path;
    private string? _flangeV2Path;

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

        // Prefer the v1 pair explicitly — manually reloaded copies may sit
        // under a "v2" folder from the owner's manual tests.
        var pairFile = Directory
            .EnumerateFiles(CatalogRoot, "Фланцевая пара.rfa", SearchOption.AllDirectories)
            .OrderByDescending(p => HasPathSegment(p, "v1"))
            .FirstOrDefault();
        _nutV1Path = FindVersionedFile(NutName, "v1");
        _nutV2Path = FindVersionedFile(NutName, "v2");
        _flangeV2Path = FindVersionedFile(FlangeName, "v2");
        if (pairFile is null || _nutV1Path is null || _nutV2Path is null || _flangeV2Path is null)
        {
            Skip.Test("Owner test library not found (flange pair, nut or flange versioned files missing)");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConRepro_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        var pairCopy = Path.Combine(_tempDir, "Фланцевая пара.rfa");
        File.Copy(pairFile, pairCopy);
        _pairDoc = Application.OpenDocumentFile(pairCopy);

        var hashExtractor = new RevitFamilySnapshotExtractor();
        var fileHasher = new FamilyContentHasher();
        _nutV1FileHash = HashFamilyFile(hashExtractor, fileHasher, _nutV1Path);
        _nutV2FileHash = HashFamilyFile(hashExtractor, fileHasher, _nutV2Path);
    }

    private static string HashFamilyFile(
        RevitFamilySnapshotExtractor extractor, FamilyContentHasher hasher, string fullPath)
    {
        var doc = Application.OpenDocumentFile(fullPath);
        try
        {
            return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(doc))!.HexString;
        }
        finally
        {
            doc.Close(false);
        }
    }

    private static string? FindVersionedFile(string familyName, string versionFolder)
    {
        // Strict: only a file under the requested version path segment
        // qualifies — a silent fallback to an arbitrary version would fail
        // the contracts with a confusing hash mismatch instead of a clear
        // Skip.
        return Directory
            .EnumerateFiles(CatalogRoot, familyName + ".rfa", SearchOption.AllDirectories)
            .FirstOrDefault(p => HasPathSegment(p, versionFolder));
    }

    private static bool HasPathSegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar).Contains(segment, StringComparer.OrdinalIgnoreCase);
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
    public async Task ReloadNestedV2_IntoRealFlangePair_RawReload_DecisiveOutcome()
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
        SmartConLogger.Info($"RealRepro: embedded nut BEFORE reload = {beforeHash[..8]} (expect v1={_nutV1FileHash})");

        // Fixture precondition: the pair must embed the nut v1 — otherwise
        // the reload outcome below cannot be interpreted (owner library
        // drift check; the library is mutated by manual tests).
        await Assert.That(beforeHash).IsEqualTo(_nutV1FileHash);

        var v2Path = _nutV2Path!;
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
            $"RealRepro: embedded nut AFTER reload = {afterHash[..8]} (v1={_nutV1FileHash}, v2={_nutV2FileHash})");

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
                            $"RealRepro: PROJECT nut = {projectHash[..8]} (v1={_nutV1FileHash}, v2={_nutV2FileHash})");
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

        // 2026-08-12 fixture drift: the owner's update-flow manual test
        // rewrote the v2 nut file (v1↔v2 no longer differ ONLY by the
        // parameter group), and with a substantively-differing incoming file
        // the RAW unpoked reload LANDS v2 — the 2026-08-11 no-op wall
        // (probes: every variant returned success while the embedded
        // definition stayed v1) was specific to group-only-diff content.
        // The durable production hazard is neither "wall" nor "landing" but
        // a SILENT PARTIAL merge (an embedded hash matching neither file) —
        // that is what this sentinel asserts now. The StaleFamilyUpdater
        // post-verify remains the arbiter in both outcomes.
        var landed = afterHash == _nutV2FileHash;
        var walled = afterHash == _nutV1FileHash;
        SmartConLogger.Info(
            $"RealRepro: raw reload outcome = {(landed ? "LANDED v2" : walled ? "WALL (stayed v1)" : "PARTIAL MERGE")}");
        await Assert.That(loaded).IsTrue();
        await Assert.That(landed || walled).IsTrue();
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
        var nutV1Path = _nutV1Path!;
        var nutV2Path = _nutV2Path!;

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
                    $"RealRepro V{(placeInstance ? 2 : 1)}: nut before = {HashNut(host)[..8]} (expect v1={_nutV1FileHash}), instance placed={placeInstance}, symbol={symbol?.Name ?? "<none>"}");

                var options = new RevitFamilyLoadOptions(true, null, null, null);
                var loaded = false;
                using (var tx = new Transaction(host, "Reload nut v2"))
                {
                    tx.Start();
                    loaded = host.LoadFamily(nutV2Path, options, out _);
                    tx.Commit();
                }
                SmartConLogger.Info(
                    $"RealRepro V{(placeInstance ? 2 : 1)}: LoadFamily(v2) returned {loaded}, nut after = {HashNut(host)[..8]} (v1={_nutV1FileHash}, v2={_nutV2FileHash})");
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
        var nutV1Path = _nutV1Path!;
        var nutV2Path = _nutV2Path!;

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
        var nutV1Path = _nutV1Path!;
        var nutV2Path = _nutV2Path!;

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
                $"RealRepro A: doc-to-doc LoadFamily returned {(result is null ? "null" : "Family")}, nut after = {Hash()[..8]} (v1={_nutV1FileHash}, v2={_nutV2FileHash})");
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
#if REVIT2022_OR_GREATER
                childV2.FamilyManager.AddParameter(
                    "ProbeParam",
                    new ForgeTypeId("autodesk.parameter.group:general-1.0.0"),
                    new ForgeTypeId("autodesk.spec.aec:length-2.0.0"),
                    false);
#else
                childV2.FamilyManager.AddParameter(
                    "ProbeParam",
                    BuiltInParameterGroup.PG_GENERAL,
                    ParameterType.Length,
                    false);
#endif
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
        var nutV1Path = _nutV1Path!;
        var nutV2Path = _nutV2Path!;

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
                    $"RealRepro C [project]: reload returned {loaded}, nut in project = {hash[..8]} (v1={_nutV1FileHash}, v2={_nutV2FileHash})");
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
        var nutV1Path = _nutV1Path!;
        var nutV2Path = _nutV2Path!;

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
            SmartConLogger.Info($"RealRepro D: before wrapper load, nut = {HashNut(host)[..8]} (v1={_nutV1FileHash})");

            // Doc-to-doc: the wrapper carries nut v2 as its shared nested;
            // the load must raise OnSharedFamilyFound for the nut →
            // source=Family (RevitFamilyLoadOptions default branch).
            var result = wrapper.LoadFamily(host, new RevitFamilyLoadOptions(true, null, null, null));
            SmartConLogger.Info(
                $"RealRepro D: wrapper doc-to-doc returned {(result is null ? "null" : "Family")}, nut after = {HashNut(host)[..8]} (v1={_nutV1FileHash}, v2={_nutV2FileHash})");
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
    public async Task VerificationHash_GroupExcluded_BothMatch_Documented()
    {
        // #209 (2026-08-11, owner decision after the poke breakthrough).
        // Supersedes the round-5 STRICT contract: it is now PROVEN (probes
        // + the owner's manual UI test on this library) that NO reload path
        // — API or UI, even a full overwrite landing new types — ever
        // propagates a parameter's group, so the verification-grade hash
        // FHV10 does not hash groups at all (the one non-transferable
        // field — embedded pollution of anything else disproved by probe).
        // On the real library after a plain path-load:
        //   · flange (depth-1): the reload lands v2's params/formulas,
        //     the group stays v1 → group-excluded verify MATCHES the file.
        //   · nut (depth-2): the reload no-ops, but v1↔v2 differ ONLY by
        //     group → group-excluded verify MATCHES (nothing transferable).
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();

        string EmbeddedVerifyHash(string familyName)
        {
            var nested = new FilteredElementCollector(_pairDoc!)
                .OfClass(typeof(Family)).Cast<Family>()
                .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
            var copy = _pairDoc!.EditFamily(nested);
            try { return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString; }
            finally { copy.Close(false); }
        }

        string FileVerifyHash(string fullPath)
        {
            var doc = Application.OpenDocumentFile(fullPath);
            try { return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(doc))!.HexString; }
            finally { doc.Close(false); }
        }

        var options = new RevitFamilyLoadOptions(true, null, null, null);

        using (var tx = new Transaction(_pairDoc!, "Reload flange v2"))
        {
            tx.Start();
            _pairDoc!.LoadFamily(_flangeV2Path!, options, out _);
            tx.Commit();
        }
        var flangeEmbedded = EmbeddedVerifyHash(FlangeName);
        var flangeFile = FileVerifyHash(_flangeV2Path!);
        SmartConLogger.Info(
            $"RealRepro V-hash group-excluded: flange embedded={flangeEmbedded[..8]} file={flangeFile[..8]} equal={flangeEmbedded == flangeFile}");
        await Assert.That(flangeEmbedded).IsEqualTo(flangeFile);

        using (var tx = new Transaction(_pairDoc!, "Reload nut v2"))
        {
            tx.Start();
            _pairDoc!.LoadFamily(_nutV2Path!, options, out _);
            tx.Commit();
        }
        var nutEmbedded = EmbeddedVerifyHash(NutName);
        var nutFile = FileVerifyHash(_nutV2Path!);
        SmartConLogger.Info(
            $"RealRepro V-hash group-excluded: nut embedded={nutEmbedded[..8]} file={nutFile[..8]} equal={nutEmbedded == nutFile}");
        await Assert.That(nutEmbedded).IsEqualTo(nutFile);
    }
}
