using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent;

public sealed class AgentProjectContextProvider : IAgentProjectContextProvider
{
    private const int TopCategoryCount = 8;

    private readonly IAgentAwaitableEvent _revit;

    public AgentProjectContextProvider(IAgentAwaitableEvent revit)
    {
        _revit = revit ?? throw new ArgumentNullException(nameof(revit));
    }

    public Task<AgentProjectContext> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _revit.RaiseAsync(app => Build(app, ct), ct);
    }

    private static AgentProjectContext Build(object app, CancellationToken ct)
    {
        var uiApplication = app as UIApplication;
        return BuildDocument(uiApplication?.ActiveUIDocument?.Document, ct);
    }

    internal static AgentProjectContext BuildDocument(Document? doc, CancellationToken ct)
    {
        if (doc is null)
        {
            return new AgentProjectContext
            {
                HasDocument = false,
                DocumentId = "none",
                Title = "(no document open)",
                RevitVersion = "unknown"
            };
        }

        var levelCount = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .GetElementCount();

        var viewCount = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Count(view => !view.IsTemplate
                           && view.ViewType != ViewType.DrawingSheet
                           && view.ViewType != ViewType.Internal
                           && view.ViewType != ViewType.ProjectBrowser
                           && view.ViewType != ViewType.SystemBrowser
                           && view.ViewType != ViewType.Undefined);

        var sheetCount = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .GetElementCount();

        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            if (++scanned % 1000 == 0)
                ct.ThrowIfCancellationRequested();

            var name = AgentElementCardFactory.GetCategoryName(element.Category);
            if (string.IsNullOrEmpty(name))
                continue;

            var categoryName = name!;
            categories.TryGetValue(categoryName, out var count);
            categories[categoryName] = count + 1;
        }

        var topCategories = categories
            .OrderByDescending(pair => pair.Value)
            .Take(TopCategoryCount)
            .Select(pair => pair.Key)
            .ToList();

        var activeView = doc.ActiveView;

        return new AgentProjectContext
        {
            HasDocument = true,
            DocumentId = AgentDocumentIdentity.GetDocumentId(doc),
            Title = doc.Title,
            PathName = string.IsNullOrWhiteSpace(doc.PathName) ? null : doc.PathName,
            RevitVersion = AgentDocumentIdentity.GetRevitVersion(doc),
            Units = AgentUnitsFormat.GetLengthUnitsLabel(doc),
            IsWorkshared = doc.IsWorkshared,
            IsModified = doc.IsModified,
            ActiveViewId = activeView?.Id.GetValue(),
            ActiveViewName = activeView?.Name,
            ActiveViewType = activeView?.ViewType.ToString(),
            LevelCount = levelCount,
            ViewCount = viewCount,
            SheetCount = sheetCount,
            TopCategories = topCategories
        };
    }
}