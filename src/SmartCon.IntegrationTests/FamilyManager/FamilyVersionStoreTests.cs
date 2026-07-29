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
/// RevitFamilyVersionStore (ADR-030, Stale Detection v2): ExtensibleStorage
/// маркер SmartCon_FamilyVersion_v1 на реальном Family в проекте.
/// Roundtrip через LoadFamily sample-семейства Autodesk.
/// </summary>
public sealed class FamilyVersionStoreTests : RevitApiTest
{
    private Document? _document;
    private RevitFamilyVersionStore? _store;
    private ElementId? _markedFamilyId;
    private ElementId? _unmarkedFamilyId;

    private Document Doc => _document!;
    private RevitFamilyVersionStore Store => _store!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void LoadFamilies()
    {
        var basicPath = SampleFiles.FindSample(Application, "rac_basic_sample_family.rfa");
        var advancedPath = SampleFiles.FindSample(Application, "rac_advanced_sample_family.rfa");
        if (basicPath is null || advancedPath is null)
        {
            Skip.Test("Sample-семейства Autodesk не найдены в Samples установленного Revit");
        }

        _document = Application.NewProjectDocument(UnitSystem.Metric);
        var transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _store = new RevitFamilyVersionStore(transactions);

        transactions.RunInTransaction(Doc, "Load families", doc =>
        {
            if (!doc.LoadFamily(basicPath, out var basic))
            {
                basic = FindFamily(doc, "rac_basic_sample_family");
            }

            if (!doc.LoadFamily(advancedPath, out var advanced))
            {
                advanced = FindFamily(doc, "rac_advanced_sample_family");
            }

            _markedFamilyId = basic?.Id ?? throw new InvalidOperationException("Не удалось загрузить sample-семейство");
            _unmarkedFamilyId = advanced?.Id ?? throw new InvalidOperationException("Не удалось загрузить sample-семейство");
        });
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        _document?.Close(false);
    }

    [Test]
    public async Task WriteThenRead_RoundTripsAllMarkerFields()
    {
        // Arrange
        var marker = new FamilyVersion(
            FamilyVersion.CurrentSchemaVersion,
            "catalog-item-42",
            "v3",
            new DateTimeOffset(2026, 7, 29, 12, 30, 0, TimeSpan.Zero),
            int.Parse(Application.VersionNumber));

        // Act
        Store.WriteToLoadedFamily(Doc, _markedFamilyId!, marker);
        var readBack = Store.ReadFromLoadedFamily(Doc, _markedFamilyId!);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(readBack).IsNotNull();
            await Assert.That(readBack!.CatalogItemId).IsEqualTo("catalog-item-42");
            await Assert.That(readBack.VersionLabel).IsEqualTo("v3");
            await Assert.That(readBack.LoadedAtUtc).IsEqualTo(marker.LoadedAtUtc);
            await Assert.That(readBack.SourceRevitVersion).IsEqualTo(marker.SourceRevitVersion);
        }
    }

    [Test]
    public async Task ReadMany_MixedFamilies_ReturnsMarkerOrNullWithoutThrowing()
    {
        // Arrange — маркер только на первом семействе
        Store.WriteToLoadedFamily(Doc, _markedFamilyId!,
            new FamilyVersion(1, "item-1", "v1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 2025));

        // Act — семейство без маркера и невалидный id не должны ронять чтение
        var result = Store.ReadManyFromDocument(Doc,
            [_markedFamilyId!, _unmarkedFamilyId!, ElementId.InvalidElementId]);

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(result.Count).IsEqualTo(2);
            await Assert.That(result[_markedFamilyId!]).IsNotNull();
            await Assert.That(result[_unmarkedFamilyId!]).IsNull();
        }
    }

    private static Autodesk.Revit.DB.Family? FindFamily(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
