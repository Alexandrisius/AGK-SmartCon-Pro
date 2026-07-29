using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// RevitFamilySnapshotExtractor + FamilyContentHasher (ADR-056, FHV3):
/// детерминизм content hash — один и тот же .rfa, открытый дважды, обязан дать
/// идентичный хэш. Это фундамент дедупликации каталога FamilyManager.
/// </summary>
public sealed class FamilySnapshotExtractorTests : RevitApiTest
{
    [Test]
    public async Task ExtractFromFamilyDocument_SameFileTwice_ProducesIdenticalContentHash()
    {
        // Arrange
        var path = SampleFiles.FindSample(Application, "rme_basic_sample_family.rfa");
        if (path is null)
        {
            Skip.Test("Sample-семейство rme_basic_sample_family.rfa не найдено");
        }

        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();

        // Act — два независимых цикла open → extract → close
        var first = ExtractSnapshot(extractor, path);
        var second = ExtractSnapshot(extractor, path);

        var firstHash = hasher.ComputeForLoadable(first);
        var secondHash = hasher.ComputeForLoadable(second);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(firstHash).IsNotNull();
            await Assert.That(firstHash!.HexString).IsEqualTo(secondHash!.HexString);
            await Assert.That(firstHash.FormatVersion).IsEqualTo(FamilyContentHashFormat.CurrentVersion);
            await Assert.That(first.FamilyName).IsNotEmpty();
            await Assert.That(first.Types.Count).IsGreaterThan(0);
        }
    }

    private FamilySnapshot ExtractSnapshot(RevitFamilySnapshotExtractor extractor, string path)
    {
        var document = Application.OpenDocumentFile(path);
        try
        {
            return extractor.ExtractFromFamilyDocument(document);
        }
        finally
        {
            document.Close(false);
        }
    }
}
