using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent;

/// <summary>
/// Opaque marker of the document state used by the agent tools. Revit's
/// <see cref="DocumentVersion"/> combines a GUID that changes as the model is
/// edited with the number of saves; the pair lets the model tell "the object does
/// not exist" from "the model was rolled back or reopened between two calls"
/// (external run 6, state stability request).
/// </summary>
internal static class AgentDocumentState
{
    internal static string? Capture(Document doc)
    {
        try
        {
            using (var version = Document.GetDocumentVersion(doc))
            {
                var guid = version.VersionGUID.ToString("N");
                if (guid.Length > 12)
                    guid = guid.Substring(0, 12);

                return guid + ":" + version.NumberOfSaves;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Explanation appended to every *_not_found failure: the current document
    /// version travels with the error, so "the object does not exist" can be told
    /// apart from "the model was rolled back or reopened" without a second call.
    /// </summary>
    internal static string NotFoundHint(string? version)
    {
        var marker = string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : " (documentVersion " + version + ")";
        return " The object is absent in the current model state" + marker +
               ": if it existed in an earlier call, the model was rolled back, reopened " +
               "or changed by someone else — re-read the model with the matching list_* " +
               "tool and retry with fresh ids.";
    }
}