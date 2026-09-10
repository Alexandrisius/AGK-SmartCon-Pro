using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Sharing;

namespace SmartCon.IntegrationTests.ProjectManagement;

/// <summary>
/// RevitViewRepository: список видов для окна Share Settings —
/// шаблоны и системные браузеры исключены, сортировка по имени.
/// </summary>
public sealed class ViewRepositoryTests : ProjectViewsFixture
{
    [Test]
    public async Task GetAllViews_ExcludesTemplatesAndSortsByName()
    {
        // Arrange
        var repository = new RevitViewRepository();

        // Act
        var views = repository.GetAllViews(Doc);
        var names = views.Select(v => v.Name).ToList();

        // Assert
        using (Assert.Multiple())
        {
            await Assert.That(names).Contains(KeepViewName);
            await Assert.That(names).Contains(DeleteViewName);
            await Assert.That(names).DoesNotContain(TemplateViewName);
            await Assert.That(names.IndexOf(KeepViewName)).IsLessThan(names.IndexOf(DeleteViewName));
        }
    }
}
