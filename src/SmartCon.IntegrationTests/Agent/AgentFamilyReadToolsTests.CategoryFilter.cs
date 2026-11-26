using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// list_families category filter (S.44): a string, an array and OST_* names go
/// through AgentCategoryResolver, so the Russian locale no longer turns them
/// into "0 categories"; an unknown name is an honest unknown_category with a
/// suggestion instead of an empty list.
/// </summary>
public sealed partial class AgentFamilyReadToolsTests
{
    [Test]
    public async Task ListFamilies_CategoryArrayWithOstAndLocalizedName_FindsTheFamily()
    {
        var family = (RevitFamily)Doc.GetElement(ElementIdCompat.Create(Seed.FamilyId))!;
        var category = family.FamilyCategory;
        if (category is null)
            Skip.Test("У загруженного семейства нет категории");

        var ostName = ((BuiltInCategory)category!.Id.GetValue()).ToString();
        if (!ostName.StartsWith("OST_", StringComparison.Ordinal))
            Skip.Test("У категории семейства нет OST_-имени: " + ostName);

        var queries = new[]
        {
            "{\"category\":\"" + ostName + "\",\"compact\":false}",
            "{\"category\":[\"" + ostName + "\",\"" + category.Name + "\"],\"compact\":false}",
            "{\"category\":\"" + category.Id.GetValue() + "\",\"compact\":false}"
        };

        foreach (var query in queries)
        {
            var tool = new ListFamiliesTool(new StubRevitContext(Doc));
            var result = tool.ExecuteForTests(Doc, Parse(query), CancellationToken.None);

            await Assert.That(result.Success).IsTrue();
            await Assert.That(ContainsFamily(Data(result), Seed.FamilyId)).IsTrue();
        }
    }

    [Test]
    public async Task ListFamilies_UnknownCategory_IsReportedWithSuggestion()
    {
        var tool = new ListFamiliesTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"category\":[\"OST_NoSuchCategory\"]}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("unknown_category");
        await Assert.That(result.Message ?? string.Empty).Contains("OST_NoSuchCategory");
    }
}
