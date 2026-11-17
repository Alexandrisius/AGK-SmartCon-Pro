using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;

namespace SmartCon.Revit.Agent;

internal static class AgentDocumentIdentity
{
    internal static string GetDocumentId(Document doc)
    {
        var location = string.IsNullOrWhiteSpace(doc.PathName) ? doc.Title : doc.PathName;
        var projectUniqueId = doc.ProjectInformation?.UniqueId ?? string.Empty;
        var centralGuid = TryGetCentralGuid(doc);
        return string.IsNullOrEmpty(centralGuid)
            ? location + "|" + projectUniqueId
            : location + "|" + projectUniqueId + "|" + centralGuid;
    }

    internal static string? TryGetCentralGuid(Document doc)
    {
        if (!doc.IsWorkshared)
            return null;

        try
        {
            return doc.WorksharingCentralGUID.ToString("D");
        }
        catch (InapplicableDataException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string GetRevitVersion(Document doc)
    {
        try
        {
            return doc.Application.VersionNumber;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}