using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// #209 contract tests (2026-08-11) for the poke + doc-to-doc nested
/// reload — the mechanism that broke the "diverged-lineage" wall
/// documented in <c>RealLibraryNestedReloadReproTests</c>. Proven facts
/// these contracts pin down (probes + owner's manual tests, real library):
///  1. Revit's changedness check is a dirty flag, not a content diff:
///     a NET-ZERO in-memory edit (add + remove a scratch parameter, two
///     commits, never saved) flips it — the owner reproduced the UI
///     overwrite dialog with exactly this cycle on a READ-ONLY file.
///  2. With the stamp flipped, doc-to-doc LoadFamily takes the full merge
///     path even through the API: a new type poked into the source doc
///     lands in the embedded definition inside the pair (depth-2 hoisted,
///     family in use via a non-shared assembly).
///  3. Parameter groups NEVER propagate on any merge (API or UI) — the
///     verification-grade hash FHV8V excludes them (see
///     <c>FamilyContentHasher.ComputeForEmbeddedVerification</c>), so the
///     nut (v1↔v2 differ ONLY by group) verifies as up-to-date and the
///     flange (regroup + real param/formula diff) verifies after its
///     content lands.
/// The production seam under test is
/// <c>RevitFamilyLoadService.ReloadNestedInFamilyDocument</c> via
/// <c>ReloadFamilyPreservingLoadedTypesAsync</c> on the real flange pair.
/// </summary>
public sealed class NestedReloadPokeContractTests : RevitApiTest
{
    private static string CatalogRoot =>
        Environment.GetEnvironmentVariable("SMARTCON_TEST_CATALOG")
        is { Length: > 0 } overridePath
            ? overridePath
            : @"d:\Project\dotNET\00_Архив\Библиотеки семейств\Тест";

    private const string NutName = "PPR-C0807-F-Гайка-ГОСТ_5915_70-PIEC-PL-0108-G3";
    private const string NutV1Hash = "CBA8FDEA";
    private const string FlangeName = "PPR-E1401-N-Фланец-ПлоскийПриварной-ГОСТ_33259_2015-PIFT-PL-0104-G3";

    private string? _tempDir;
    private Document? _pairDoc;
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
        _nutV2Path = FindVersionedFile(NutName);
        _flangeV2Path = FindVersionedFile(FlangeName);
        if (pairFile is null || _nutV2Path is null || _flangeV2Path is null)
        {
            Skip.Test("Owner test library not found (flange pair, nut v2 or flange v2 missing)");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConPoke_{Guid.NewGuid().ToString("N")}");
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
    public async Task PokeDocToDoc_DivergedLineageFamily_LandsContent()
    {
        // The core mechanism contract: source poked with a REAL in-memory
        // change (a new type — an unmissable marker), then API doc-to-doc
        // into the pair. The nut resists every unpoked API variant (see
        // RealLibraryNestedReloadReproTests) — with the poke the merge
        // lands and the marker type appears in the embedded definition.
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();

        var beforeHash = HashEmbeddedNut(_pairDoc!, extractor, hasher);
        var beforeTypes = EmbeddedNutSymbolNames(_pairDoc!);
        SmartConLogger.Info($"PokeContract: embedded nut BEFORE = {beforeHash[..8]} types=[{beforeTypes}] (v1={NutV1Hash})");

        var sourceDoc = Application.OpenDocumentFile(_nutV2Path!);
        Family? pushed;
        try
        {
            using (var tx = new Transaction(sourceDoc, "Poke new type"))
            {
                tx.Start();
                sourceDoc.FamilyManager.NewType("SmartConPoke");
                var status = tx.Commit();
                SmartConLogger.Info($"PokeContract: source poke NewType commit={status}");
            }

            // Doc-to-doc LoadFamily manages its own transaction — the
            // target document must not be modifiable at call time.
            pushed = sourceDoc.LoadFamily(_pairDoc!, new RevitFamilyLoadOptions(true, null, null, null));
        }
        finally
        {
            sourceDoc.Close(false);
        }

        var afterTypes = EmbeddedNutSymbolNames(_pairDoc!);
        var afterHash = HashEmbeddedNut(_pairDoc!, extractor, hasher);
        SmartConLogger.Info(
            $"PokeContract: doc-to-doc returned {(pushed is null ? "null" : "Family")}, " +
            $"embedded AFTER = {afterHash[..8]} types=[{afterTypes}]");

        await Assert.That(pushed).IsNotNull();
        await Assert.That(afterTypes).Contains("SmartConPoke");
        await Assert.That(afterHash[..8]).IsNotEqualTo(NutV1Hash);
    }

    [Test]
    public async Task ProductionUpdate_Nut_GroupOnlyDiff_SucceedsAndVerifies()
    {
        // End-to-end through the production seam (ReloadNestedInFamilyDocument
        // with the poke path): the nut's v1↔v2 diff is ONLY the parameter
        // group — nothing propagates on merge, and FHV8V excludes groups,
        // so the update succeeds and the embedded content verifies against
        // the resolved v2 file. Weak on content (there is nothing to land)
        // by design — the content-landing proof is the sibling contracts.
        var result = await CreateService().ReloadFamilyPreservingLoadedTypesAsync(
            new FamilyResolvedFile(_nutV2Path!, null, null),
            overwriteParameterValues: true);

        var embeddedVerify = EmbeddedVerifyHash(_pairDoc!, NutName);
        var fileVerify = FileVerifyHash(_nutV2Path!);
        SmartConLogger.Info(
            $"PokeContract: production nut update success={result.Success} status={result.Status}, " +
            $"embeddedVerify={embeddedVerify[..8]} fileVerify={fileVerify[..8]} equal={embeddedVerify == fileVerify}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(embeddedVerify).IsEqualTo(fileVerify);
    }

    [Test]
    public async Task ProductionUpdate_Flange_RealContentDiff_LandsAndVerifies()
    {
        // The flange v1↔v2 diff is a regroup PLUS real propagating content
        // (formula + a removed parameter, 56→55 params). The production
        // poke path must land the content (embedded loadable hash changes)
        // and the group-excluded verification must pass.
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var beforeHash = HashEmbedded(_pairDoc!, FlangeName, extractor, hasher);

        var result = await CreateService().ReloadFamilyPreservingLoadedTypesAsync(
            new FamilyResolvedFile(_flangeV2Path!, null, null),
            overwriteParameterValues: true);

        var afterHash = HashEmbedded(_pairDoc!, FlangeName, extractor, hasher);
        var embeddedVerify = EmbeddedVerifyHash(_pairDoc!, FlangeName);
        var fileVerify = FileVerifyHash(_flangeV2Path!);
        SmartConLogger.Info(
            $"PokeContract: production flange update success={result.Success} status={result.Status}, " +
            $"loadable {beforeHash[..8]}→{afterHash[..8]}, " +
            $"embeddedVerify={embeddedVerify[..8]} fileVerify={fileVerify[..8]} equal={embeddedVerify == fileVerify}");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(afterHash).IsNotEqualTo(beforeHash);
        await Assert.That(embeddedVerify).IsEqualTo(fileVerify);
    }

    private RevitFamilyLoadService CreateService()
    {
        var context = new StubRevitContext(_pairDoc!);
        return new RevitFamilyLoadService(context, new RevitTransactionService(context));
    }

    private static string? FindVersionedFile(string familyName)
    {
        // Strict: only a file under a "v2" path segment qualifies — a
        // silent fallback to an arbitrary version would fail the contracts
        // with a confusing hash mismatch instead of a clear Skip.
        return Directory
            .EnumerateFiles(CatalogRoot, familyName + ".rfa", SearchOption.AllDirectories)
            .FirstOrDefault(p => HasPathSegment(p, "v2"));
    }

    private static bool HasPathSegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar).Contains(segment, StringComparer.OrdinalIgnoreCase);
    }

    private static string HashEmbeddedNut(Document pairDoc, RevitFamilySnapshotExtractor extractor, FamilyContentHasher hasher)
    {
        return HashEmbedded(pairDoc, NutName, extractor, hasher);
    }

    private static string HashEmbedded(Document pairDoc, string familyName, RevitFamilySnapshotExtractor extractor, FamilyContentHasher hasher)
    {
        var nested = FindNestedFamily(pairDoc, familyName);
        var copy = pairDoc.EditFamily(nested);
        try
        {
            return hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(copy))!.HexString;
        }
        finally
        {
            copy.Close(false);
        }
    }

    private static string EmbeddedVerifyHash(Document pairDoc, string familyName)
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var nested = FindNestedFamily(pairDoc, familyName);
        var copy = pairDoc.EditFamily(nested);
        try
        {
            return hasher.ComputeForEmbeddedVerification(extractor.ExtractFromFamilyDocument(copy))!.HexString;
        }
        finally
        {
            copy.Close(false);
        }
    }

    private string FileVerifyHash(string path)
    {
        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();
        var doc = Application.OpenDocumentFile(path);
        try
        {
            return hasher.ComputeForEmbeddedVerification(extractor.ExtractFromFamilyDocument(doc))!.HexString;
        }
        finally
        {
            doc.Close(false);
        }
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }

    private static string EmbeddedNutSymbolNames(Document pairDoc)
    {
        var nested = FindNestedFamily(pairDoc, NutName);
        return string.Join(",", nested.GetFamilySymbolIds()
            .Select(id => (pairDoc.GetElement(id) as FamilySymbol)?.Name ?? "?")
            .OrderBy(n => n, StringComparer.Ordinal));
    }
}
