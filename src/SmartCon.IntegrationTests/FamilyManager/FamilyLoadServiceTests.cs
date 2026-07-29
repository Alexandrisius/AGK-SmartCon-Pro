using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// RevitFamilyLoadService: реальная загрузка .rfa в проект через doc.LoadFamily
/// с ретраями сервиса. Статусы Loaded/Current — то, что видит пользователь
/// FamilyManager при загрузке семейства.
/// </summary>
public sealed class FamilyLoadServiceTests : RevitApiTest
{
    private Document? _document;
    private RevitFamilyLoadService? _service;
    private string? _samplePath;

    private Document Doc => _document!;
    private RevitFamilyLoadService Service => _service!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void OpenDocument()
    {
        _samplePath = SampleFiles.FindSample(Application, "rac_basic_sample_family.rfa");
        if (_samplePath is null)
        {
            Skip.Test("Sample-семейство rac_basic_sample_family.rfa не найдено");
        }

        _document = Application.NewProjectDocument(UnitSystem.Metric);
        var transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _service = new RevitFamilyLoadService(new StubRevitContext(Doc), transactions);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        _document?.Close(false);
    }

    [Test]
    public async Task LoadFamilyAsync_FreshProject_LoadsFamilyIntoDocument()
    {
        // Act
        var result = await Service.LoadFamilyAsync(
            new FamilyResolvedFile(_samplePath!, null, null),
            FamilyLoadOptions.Default);

        // Assert
        var familyLoaded = new FilteredElementCollector(Doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .Any(f => f.Name.Equals("rac_basic_sample_family", StringComparison.OrdinalIgnoreCase));

        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsTrue();
            await Assert.That(result.Status).IsEqualTo(FamilyLoadStatus.Loaded);
            await Assert.That(familyLoaded).IsTrue();
        }
    }

    [Test]
    public async Task LoadFamilyAsync_SameFileTwice_SecondLoadReportsCurrent()
    {
        // Arrange — первая загрузка
        _ = await Service.LoadFamilyAsync(
            new FamilyResolvedFile(_samplePath!, null, null),
            FamilyLoadOptions.Default);

        // Act — повторная: VersionGuid не изменился → «already up-to-date»
        var result = await Service.LoadFamilyAsync(
            new FamilyResolvedFile(_samplePath!, null, null),
            FamilyLoadOptions.Default);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsTrue();
            await Assert.That(result.Status).IsEqualTo(FamilyLoadStatus.Current);
        }
    }
}
