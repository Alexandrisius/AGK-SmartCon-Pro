using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services;

public static class CommandHelper
{
    public static void InitializeContext(object uiApplication)
    {
        var writer = ServiceHost.GetService<IRevitContextWriter>();
        writer.SetContext(uiApplication);
    }

    public static Document GetDocument()
    {
        var context = ServiceHost.GetService<IRevitContext>();
        return context.GetDocument();
    }
}
