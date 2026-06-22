using System.IO;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

/// <summary>
/// Verifies the three-step sidecar (.txt) resolution chain in
/// <c>LocalFamilyImportService.PrepareManagedRfaAsync</c>:
/// 1. sidecar next to the .rfa being imported (filePath);
/// 2. sidecar next to the ORIGINAL source path (OriginalSourcePath) — this
///    is the path that fixes the "Import Active File" bug where the
///    .rfa was copied to a temp folder but the .txt stayed next to the
///    original document;
/// 3. sidecar from a previous version in managed storage (legacy).
/// All these cases are expected to succeed silently when the file
/// is missing (no error, no types added).
/// </summary>
public sealed class LocalFamilyImportServiceTypeCatalogTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyImportServiceTypeCatalogTests()
    {
        _fixture = new TempCatalogFixture();

        var hasher = new Sha256FileHasher();
        var metadataService = new FileNameOnlyMetadataExtractionService(hasher);
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
    }

    [Fact]
    public async Task ImportFile_TxtAlongsideRfa_TypeCatalogImported()
    {
        var rfa = _fixture.CreateFakeRfaFile("SidecarOK.rfa");
        var txt = Path.ChangeExtension(rfa, ".txt");
        File.WriteAllText(txt, """
            ,Width##length##millimeters
            TypeA,100
            TypeB,150
            """);

        var result = await _importService.ImportFileAsync(
            new FamilyImportRequest(rfa, 2025, "Pipes", null, null));

        Assert.True(result.Success);

        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(result.CatalogItemId!);
        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "TypeA");
        Assert.Contains(types, t => t.Name == "TypeB");
    }

    [Fact]
    public async Task ImportFile_TxtOnlyAlongsideOriginal_TypeCatalogImported()
    {
        // This is the actual bug scenario: the .rfa being imported is a
        // temp copy (filePath) that does NOT have a .txt next to it, but
        // the ORIGINAL .rfa path DOES have a .txt.
        var originalDir = Path.Combine(_fixture.TempDir, "original");
        var importedDir = Path.Combine(_fixture.TempDir, "imported");
        Directory.CreateDirectory(originalDir);
        Directory.CreateDirectory(importedDir);

        var originalRfa = Path.Combine(originalDir, "BugRepro.rfa");
        var originalTxt = Path.Combine(originalDir, "BugRepro.txt");
        var importedRfa = Path.Combine(importedDir, "BugRepro.rfa");

        File.WriteAllText(originalRfa, "ORIGINAL_RFA");
        File.WriteAllText(originalTxt, """
            ,Length##length##millimeters
            DN50,50
            DN100,100
            DN150,150
            """);
        File.WriteAllText(importedRfa, "TEMP_COPY_RFA");

        var request = new FamilyImportRequest(
            FilePath: importedRfa,
            RevitMajorVersion: 2025,
            Category: "Pipes",
            Tags: null,
            Description: null,
            CategoryId: null,
            FamilySource: "loadable",
            RevitCategory: null,
            FileName: null,
            OriginalSourcePath: originalRfa);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success, result.ErrorMessage);

        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(result.CatalogItemId!);
        Assert.Equal(3, types.Count);
        Assert.Contains(types, t => t.Name == "DN50");
        Assert.Contains(types, t => t.Name == "DN100");
        Assert.Contains(types, t => t.Name == "DN150");

        // Managed .rfa must exist and must NOT have a sidecar .txt copied next to it
        var item = await _fixture.GetProvider().GetFileAsync(result.FileId!);
        Assert.NotNull(item);
        var storedRfa = Path.Combine(_fixture.GetDatabaseRoot(), item.RelativePath);
        Assert.True(File.Exists(storedRfa), $"Expected managed .rfa at '{storedRfa}'");

        var storedTxt = Path.ChangeExtension(storedRfa, ".txt");
        Assert.False(File.Exists(storedTxt), $"Sidecar .txt must NOT be copied to managed storage: '{storedTxt}'");
    }

    [Fact]
    public async Task ImportFile_NoTxtAnywhere_SilentlySucceeds()
    {
        var rfa = _fixture.CreateFakeRfaFile("NoCatalog.rfa");

        var result = await _importService.ImportFileAsync(
            new FamilyImportRequest(rfa, 2025, null, null, null));

        Assert.True(result.Success);

        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(result.CatalogItemId!);
        Assert.Empty(types);
    }

    [Fact]
    public async Task ImportFile_OriginalSameAsFilePath_DoesNotDuplicate()
    {
        // If FilePath == OriginalSourcePath the chain must not look twice
        // and the sidecar should still be imported once.
        var rfa = _fixture.CreateFakeRfaFile("SelfRef.rfa");
        var txt = Path.ChangeExtension(rfa, ".txt");
        File.WriteAllText(txt, """
            ,Param##other##
            TypeX,Value
            """);

        var request = new FamilyImportRequest(
            FilePath: rfa,
            RevitMajorVersion: 2025,
            Category: null,
            Tags: null,
            Description: null,
            CategoryId: null,
            FamilySource: "loadable",
            RevitCategory: null,
            FileName: null,
            OriginalSourcePath: rfa);

        var result = await _importService.ImportFileAsync(request);

        Assert.True(result.Success);

        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(result.CatalogItemId!);
        Assert.Single(types);
        Assert.Equal("TypeX", types[0].Name);
    }

    [Fact]
    public async Task UpdateFamily_PropagatesOriginalSourcePath_TxtImported()
    {
        // Same bug, but for the "update existing version" path:
        // ImportFamilyAsync first, then UpdateFamilyAsync with a temp .rfa
        // whose ORIGINAL path has the .txt.
        var originalDir = Path.Combine(_fixture.TempDir, "originalV2");
        var importedDir = Path.Combine(_fixture.TempDir, "importedV2");
        Directory.CreateDirectory(originalDir);
        Directory.CreateDirectory(importedDir);

        var originalRfa = Path.Combine(originalDir, "Updatable.rfa");
        var originalTxt = Path.Combine(originalDir, "Updatable.txt");
        var tempRfa = Path.Combine(importedDir, "Updatable.rfa");

        File.WriteAllText(originalRfa, "ORIGINAL");
        File.WriteAllText(tempRfa, "TEMP_V2");

        // 1) First import (no txt anywhere)
        var first = await _importService.ImportFileAsync(
            new FamilyImportRequest(originalRfa, 2024, null, null, null));
        Assert.True(first.Success);
        Assert.NotNull(first.CatalogItemId);

        // 2) Create sidecar next to ORIGINAL and update via temp copy
        File.WriteAllText(originalTxt, """
            ,Size##length##millimeters
            S50,50
            S100,100
            """);

        var updateRequest = new FamilyUpdateRequest(
            CatalogItemId: first.CatalogItemId!,
            FilePath: tempRfa,
            RevitMajorVersion: 2025,
            CategoryId: null,
            CategoryName: null,
            FileName: null,
            OriginalSourcePath: originalRfa);

        var second = await _importService.UpdateFamilyAsync(updateRequest);
        Assert.True(second.Success);
        Assert.True(second.WasNewVersion);

        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(first.CatalogItemId!);
        Assert.Equal(2, types.Count);
        Assert.Contains(types, t => t.Name == "S50");
        Assert.Contains(types, t => t.Name == "S100");
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
