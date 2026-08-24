using Nice3point.TUnit.Revit;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// RevitCategoryLabelService (#241): the curated pickable category list
/// resolves inside a real Revit session. Proves two things the unit tests
/// cannot: (1) the reflection resolution of LabelUtils.GetLabelFor
/// (BuiltInCategory) — an API that exists only since Revit 2020, R19 runs
/// on a Revit 2019 runtime where a direct call would JIT-crash — actually
/// binds on the running version; (2) the static lookup is callable from
/// the test context without an API-context exception (labels come from
/// the session resource strings, not the document).
/// </summary>
public sealed class RevitCategoryLabelServiceTests : RevitApiTest
{
    // Ленивое поле: инстанцирование сервиса с типами из SmartCon.Revit
    // должно происходить внутри теста, а не при загрузке класса
    // (правило #1, Nice3point/RevitUnit#78)
    private static RevitCategoryLabelService Service => new();

    [Test]
    public async Task GetModelCategories_ReturnsCuratedList_WithNonEmptyLabels()
    {
        var labels = Service.GetModelCategories();

        await Assert.That(labels.Count).IsGreaterThan(20);
        await Assert.That(labels.All(l => l.Ordinal != 0)).IsTrue();
        await Assert.That(labels.All(l => !string.IsNullOrWhiteSpace(l.Label))).IsTrue();
    }

    [Test]
    public async Task GetModelCategories_PipeFittingOrdinal_Present()
    {
        // OST_PipeFitting = -2008049 — the ordinal the assignment engine
        // and the family-facts rule set rely on.
        var labels = Service.GetModelCategories();

        await Assert.That(labels.Any(l => l.Ordinal == -2008049)).IsTrue();
    }

    [Test]
    public async Task GetModelCategories_OrdinalsAreDistinct()
    {
        var labels = Service.GetModelCategories();

        await Assert.That(labels.Select(l => l.Ordinal).Distinct().Count()).IsEqualTo(labels.Count);
    }
}
