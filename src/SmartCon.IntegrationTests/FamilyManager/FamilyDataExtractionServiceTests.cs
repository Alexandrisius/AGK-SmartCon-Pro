using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// RevitFamilyDataExtractionService: извлечение типов/параметров из реального
/// .rfa через OpenDocumentFile — единая точка входа импорта FamilyManager
/// (ADR-033). Плюс graceful failure на битом пути.
/// </summary>
public sealed class FamilyDataExtractionServiceTests : RevitApiTest
{
    private Document? _document;
    private RevitFamilyDataExtractionService? _service;
    private RevitFamilyDataExtractionService Service => _service!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateService()
    {
        // Сервису нужен IRevitContext только ради Application — используем
        // временный in-memory документ как источник Application.
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _service = new RevitFamilyDataExtractionService(new StubRevitContext(_document));
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        _document?.Close(false);
    }

    [Test]
    public async Task ExtractFromManagedFile_SampleFamily_ExtractsRealTypes()
    {
        // Arrange
        var path = SampleFiles.FindSample(Application, "rac_advanced_sample_family.rfa");
        if (path is null)
        {
            Skip.Test("Sample-семейство rac_advanced_sample_family.rfa не найдено");
        }

        // Act
        var result = Service.ExtractFromManagedFile(path, []);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsTrue();
            await Assert.That(result.Types.Count).IsGreaterThan(0);
            await Assert.That(result.Types[0].TypeName).IsNotEmpty();
            await Assert.That(result.RevitMajorVersion).IsEqualTo(int.Parse(Application.VersionNumber));
        }
    }

    [Test]
    public async Task ExtractFromManagedFile_MissingFile_ReturnsGracefulFailure()
    {
        // Act — битый путь не должен ронять импорт (результат, а не исключение)
        var result = Service.ExtractFromManagedFile(@"D:\nonexistent\missing.rfa", []);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsFalse();
            await Assert.That(result.ErrorMessage).Contains("File not found");
        }
    }
}
