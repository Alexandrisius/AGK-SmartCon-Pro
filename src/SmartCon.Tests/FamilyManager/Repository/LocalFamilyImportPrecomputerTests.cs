using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// Unit tests for <see cref="LocalFamilyImportPrecomputer"/>.
///
/// v2.0.0 regression: the precomputer is the single source of truth
/// for the canonical (CatalogItemId, VersionLabel, ManagedPath) triple.
/// Both the initial dialog build (<c>MapPreparedItemsToBatchItemsAsync</c>)
/// and the dialog rename handler (<c>FamilyBatchImportViewModel.OnRowNameChanged</c>)
/// go through it. The tests below pin every leg of that contract.
/// </summary>
public sealed class LocalFamilyImportPrecomputerTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;
    private readonly LocalFamilyImportPrecomputer _precomputer;

    public LocalFamilyImportPrecomputerTests()
    {
        _fixture = new TempCatalogFixture();
        var metadataService = new FileMetadataExtractionService();
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            metadataService,
            _fixture.GetTypeRepository(),
            _fixture.GetValueRepository(),
            _fixture.GetRunRepository(),
            _fixture.GetTypeCatalogBaker());
        _precomputer = new LocalFamilyImportPrecomputer(_fixture.GetProvider(), _importService);
    }

    [Fact]
    public async Task BuildTriple_NewName_AllocatesFreshGuidAndV1()
    {
        var triple = await _precomputer.BuildPrecomputedTripleAsync("Brand New Family", ".rfa");

        Assert.NotNull(triple);
        Assert.False(string.IsNullOrEmpty(triple!.CatalogItemId));
        // Fresh GUID, "N" format (no dashes), 32 hex chars
        Assert.Matches("^[0-9a-f]{32}$", triple.CatalogItemId);
        Assert.Equal("v1", triple.VersionLabel);
        Assert.EndsWith("Brand New Family.rfa", triple.ManagedPath);
        Assert.StartsWith(_fixture.GetDatabaseRoot(), triple.ManagedPath);
    }

    [Fact]
    public async Task BuildTriple_NewName_RvtExtension_HonoursExtension()
    {
        var triple = await _precomputer.BuildPrecomputedTripleAsync("Свежие трубы", ".rvt");

        Assert.NotNull(triple);
        Assert.Equal("v1", triple!.VersionLabel);
        // .rvt for system families, not .rfa.
        Assert.EndsWith("Свежие трубы.rvt", triple.ManagedPath);
    }

    [Fact]
    public async Task BuildTriple_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(await _precomputer.BuildPrecomputedTripleAsync("", ".rfa"));
        Assert.Null(await _precomputer.BuildPrecomputedTripleAsync("   ", ".rfa"));
        Assert.Null(await _precomputer.BuildPrecomputedTripleAsync(null!, ".rfa"));
    }

    [Fact]
    public async Task BuildTriple_ExistingName_ReusesIdAndAdvancesVersion()
    {
        // Seed an existing item at v1.
        var seed = _fixture.CreateFakeRfaFile("Existing Family.rfa");
        var seedResult = await _importService.ImportFileAsync(
            new FamilyImportRequest(seed, 2025, null, null, null));
        Assert.True(seedResult.Success);
        Assert.Equal("v1", seedResult.VersionLabel);

        var triple = await _precomputer.BuildPrecomputedTripleAsync("Existing Family", ".rfa");

        Assert.NotNull(triple);
        // Must reuse the same id (not allocate a fresh GUID).
        Assert.Equal(seedResult.CatalogItemId, triple!.CatalogItemId);
        // Must advance to the next version.
        Assert.Equal("v2", triple.VersionLabel);
        Assert.EndsWith("Existing Family.rfa", triple.ManagedPath);
    }

    [Fact]
    public async Task BuildTriple_NameWithInvalidChars_SanitisesFileName()
    {
        // The sanitisation rule lives in SmartCon.Core.Services.FamilyManager.SafeFileName.
        // The precomputer must go through it so the on-disk file matches
        // the path the import service will register. "bad/name?" becomes
        // "bad_name_" under Path.GetInvalidFileNameChars on Windows.
        var triple = await _precomputer.BuildPrecomputedTripleAsync("bad/name?", ".rfa");

        Assert.NotNull(triple);
        // The Path.GetInvalidFileNameChars rule replaces '/' and '?' with '_'.
        Assert.EndsWith("bad_name_.rfa", triple.ManagedPath);
    }

    [Fact]
    public async Task BuildTriple_CalledTwiceForSameNewName_ProducesDifferentGuids()
    {
        // Two batches run back-to-back must not collide: each batch's
        // "new" precomputer call allocates a fresh GUID even when the
        // display name matches. (We are NOT debouncing; the call only
        // reuses an id when the catalog already has that name.)
        var first = await _precomputer.BuildPrecomputedTripleAsync("Repeat", ".rfa");
        var second = await _precomputer.BuildPrecomputedTripleAsync("Repeat", ".rfa");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.CatalogItemId, second!.CatalogItemId);
    }

    [Fact]
    public async Task BuildTriple_AfterItemSeeded_KeepsDifferentTripleFromSecondCallWithoutSeed()
    {
        // Seed item at v1, then a new name that has not been seeded yet
        // must still allocate a fresh GUID + v1 (not get re-mapped to the
        // seeded item by accident).
        var seed = _fixture.CreateFakeRfaFile("Seeded.rfa");
        await _importService.ImportFileAsync(
            new FamilyImportRequest(seed, 2025, null, null, null));

        var seededTriple = await _precomputer.BuildPrecomputedTripleAsync("Seeded", ".rfa");
        var freshTriple = await _precomputer.BuildPrecomputedTripleAsync("Different", ".rfa");

        Assert.NotNull(seededTriple);
        Assert.NotNull(freshTriple);
        Assert.Equal("v2", seededTriple!.VersionLabel);
        Assert.Equal("v1", freshTriple!.VersionLabel);
        Assert.NotEqual(seededTriple.CatalogItemId, freshTriple.CatalogItemId);
    }

    /// <summary>
    /// v2.0.0 contract: the precomputer is a PURE compute service.
    /// It must NOT touch the file system. Directory creation is the
    /// caller's responsibility — see <c>ProcessFamilyImportAsync</c> for
    /// UC-2 and the staging helpers for UC-3/UC-4. (Earlier revisions
    /// of this service called <c>EnsureFamilyDirectories</c> eagerly,
    /// which left an empty <c>vN+1/</c> directory every time the user
    /// cancelled the dialog or the import failed downstream — exactly
    /// the orphan folders the user observed in the test run.)
    /// </summary>
    [Fact]
    public async Task BuildTriple_PureCompute_DoesNotCreateDirectory()
    {
        // Use a fresh directory under TempDir so we can verify the
        // precomputer is the ONLY thing that runs here (no other test
        // leaks a directory). The local fixture was migrated against
        // its own TempDir, so the simplest way to "look at a fresh
        // path" is to compute what the canonical managed path would
        // be for a NEW name (Status = New, no existing item), then
        // assert that the *parent directory of that path* does not
        // exist on disk in the fixture's TempDir.
        var triple = await _precomputer.BuildPrecomputedTripleAsync("BrandNewPure", ".rfa");

        Assert.NotNull(triple);
        // The parent directory of the canonical managed path must
        // NOT exist on disk yet. The downstream importer (SaveAs /
        // staging helper / ImportFileAsync) is responsible for
        // creating it. Eagerly creating it here would leave an empty
        // vN+1/ folder every time the user cancelled the dialog.
        var parentDir = Path.GetDirectoryName(triple!.ManagedPath);
        Assert.NotNull(parentDir);
        Assert.False(Directory.Exists(parentDir),
            $"Precomputer must not create '{parentDir}' eagerly — that leaves an empty vN+1/ folder when the import is cancelled");
    }

    /// <summary>
    /// v2.0.0: when the precomputer returns the (id, vN+1, path) triple
    /// for an existing item, the path must point at vN+1 (not vN).
    /// The caller is then responsible for re-resolving the path to vN
    /// when the user picks <c>OverwriteCurrent</c> — see
    /// <c>ProcessFamilyImportAsync</c>. This test pins the
    /// precomputer-only contract; the OverwriteCurrent recompute
    /// happens upstream.
    /// </summary>
    [Fact]
    public async Task BuildTriple_ExistingItem_PathPointsAtVNext_NotCurrentVersion()
    {
        // Seed v1.
        var seed = _fixture.CreateFakeRfaFile("OverwriteMe.rfa");
        var seedResult = await _importService.ImportFileAsync(
            new FamilyImportRequest(seed, 2025, null, null, null));
        Assert.True(seedResult.Success);

        var triple = await _precomputer.BuildPrecomputedTripleAsync("OverwriteMe", ".rfa");

        Assert.NotNull(triple);
        // vN+1, not vN. (vN would mean we'd accidentally overwrite
        // the existing file before the user asked us to.)
        Assert.Equal("v2", triple!.VersionLabel);
        Assert.Contains("\\v2\\", triple.ManagedPath);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
