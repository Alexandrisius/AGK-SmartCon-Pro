using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Document side of AgentCategoryResolver aliases (C6-06/C10-04): an enum-shaped
/// guess like OST_Pipes resolves to the category whose display name is "Pipes",
/// and every token offered by unknown_category suggestions is itself resolvable.
/// The pure string logic is pinned by SmartCon.Tests unit tests.
/// </summary>
public sealed class AgentCategoryResolverAliasTests : RevitApiTest
{
    private Document? _document;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task Resolve_EnumName_ResolvesExistingCategory()
    {
        var pipes = Category.GetCategory(Doc, BuiltInCategory.OST_PipeCurves);
        if (pipes is null)
            Skip.Test("В документе нет категории труб");

        var unknown = new List<string>();
        var ids = AgentCategoryResolver.Resolve(Doc, new[] { "OST_PipeCurves" }, unknown);

        await Assert.That(unknown.Count).IsEqualTo(0);
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0].GetValue()).IsEqualTo(pipes.Id.GetValue());
    }

    [Test]
    public async Task Resolve_OstShapedAliasOfDisplayName_ResolvesSameCategory()
    {
        var walls = Category.GetCategory(Doc, BuiltInCategory.OST_Walls);
        if (walls is null || string.IsNullOrWhiteSpace(walls.Name))
            Skip.Test("В документе нет категории стен с именем");

        var alias = "OST_" + walls.Name!.Replace(' ', '_');
        var unknown = new List<string>();
        var ids = AgentCategoryResolver.Resolve(Doc, new[] { alias }, unknown);

        await Assert.That(unknown.Count).IsEqualTo(0);
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0].GetValue()).IsEqualTo(walls.Id.GetValue());
    }

    [Test]
    public async Task Resolve_OstPipes_MeansThePipesCategory()
    {
        var pipes = Category.GetCategory(Doc, BuiltInCategory.OST_PipeCurves);
        if (pipes is null || !string.Equals(pipes.Name, "Pipes", StringComparison.Ordinal))
            Skip.Test("Категория труб не называется 'Pipes' в этой локали (" + pipes?.Name + ")");

        var unknown = new List<string>();
        var ids = AgentCategoryResolver.Resolve(Doc, new[] { "OST_Pipes" }, unknown);

        await Assert.That(unknown.Count).IsEqualTo(0);
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0].GetValue()).IsEqualTo(pipes.Id.GetValue());
    }

    [Test]
    public async Task SuggestClosest_UnknownToken_OffersOnlyResolvableTokens()
    {
        foreach (var guess in new[] { "OST_NoSuchCategory", "OST_PipeValveAssembly" })
        {
            var suggestions = AgentCategoryResolver.SuggestClosest(Doc, guess);
            foreach (var token in suggestions)
            {
                var unknown = new List<string>();
                var ids = AgentCategoryResolver.Resolve(Doc, new[] { token }, unknown);
                await Assert.That(unknown.Count).IsEqualTo(0);
                await Assert.That(ids.Count).IsEqualTo(1);
            }
        }

        // A same-family guess (OST_PipeValveAssembly among the OST_Pipe*
        // categories) must produce at least one usable suggestion.
        var family = AgentCategoryResolver.SuggestClosest(Doc, "OST_PipeValveAssembly");
        await Assert.That(family.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Resolve_DisplayName_ResolvesSameCategory()
    {
        var walls = Category.GetCategory(Doc, BuiltInCategory.OST_Walls);
        if (walls is null || string.IsNullOrWhiteSpace(walls.Name))
            Skip.Test("В документе нет категории стен с именем");

        var unknown = new List<string>();
        var ids = AgentCategoryResolver.Resolve(Doc, new[] { walls.Name! }, unknown);

        await Assert.That(unknown.Count).IsEqualTo(0);
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0].GetValue()).IsEqualTo(walls.Id.GetValue());
    }
}
