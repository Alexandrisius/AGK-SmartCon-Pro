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

    /// <summary>
    /// True when both wrappers represent the same open document. Revit hands
    /// out distinct managed Document instances for one open document depending
    /// on the entry point (application.Documents vs the instance returned by
    /// NewProjectDocument/OpenDocumentFile — proven by the multi-model
    /// integration tests), so reference equality is not enough: fall back to
    /// the open-document identity, the PathName. Unsaved documents (empty
    /// PathName) match by reference only.
    /// </summary>
    internal static bool IsSameOpenDocument(Document? candidate, Document? active)
    {
        if (candidate is null || active is null)
            return false;

        if (ReferenceEquals(candidate, active))
            return true;

        return !string.IsNullOrEmpty(candidate.PathName)
               && !string.IsNullOrEmpty(active.PathName)
               && string.Equals(
                   candidate.PathName,
                   active.PathName,
                   StringComparison.OrdinalIgnoreCase);
    }
}