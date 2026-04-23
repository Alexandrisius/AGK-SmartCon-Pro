using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IModelPurgeService
{
    int Purge(Document doc, PurgeOptions options, IReadOnlyList<string> keepViewNames);
}
