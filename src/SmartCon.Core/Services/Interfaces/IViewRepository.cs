using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IViewRepository
{
    List<ViewInfo> GetAllViews(Document doc);
}
