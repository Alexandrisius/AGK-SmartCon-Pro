using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Sharing;

public sealed class RevitViewRepository : IViewRepository
{
    public List<ViewInfo> GetAllViews(Document doc)
    {
#if NETFRAMEWORK
        if (doc is null) throw new ArgumentNullException(nameof(doc));
#else
        ArgumentNullException.ThrowIfNull(doc);
#endif
        var hiddenTypes = new HashSet<ViewType> { ViewType.SystemBrowser, ViewType.ProjectBrowser };

        return new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => !v.IsTemplate)
            .Where(v => !hiddenTypes.Contains(v.ViewType))
            .Select(v => new ViewInfo
            {
                Name = v.Name,
                Id = v.Id,
                ViewType = v.ViewType.ToString()
            })
            .OrderBy(v => v.Name)
            .ToList();
    }
}
