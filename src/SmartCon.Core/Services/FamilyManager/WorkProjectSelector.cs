namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Issue #186: snapshot of one open Revit document used by
/// <see cref="WorkProjectSelector"/>. Pure data — the Revit-side caller maps
/// <c>Document</c> instances onto this record so the selection logic stays
/// unit-testable without Revit API.
/// </summary>
public sealed record OpenDocumentInfo(
    string PathName,
    bool IsFamilyDocument,
    bool IsLinked,
    bool IsMiniProject);

/// <summary>
/// Issue #186: pure selection logic — picks the open document that should
/// receive focus after a reference mini-project is closed post-reimport.
/// The work project must be a real user project: not a family, not a link,
/// not another mini-project, not the reference being closed. Returns null when
/// no suitable document is open (caller then creates a blank project to move
/// focus off the active mini-project — <c>Document.Close</c> is forbidden on
/// the active document, and <c>PostableCommand.Close</c> would show a
/// "Save changes?" prompt risking an overwrite of the read-only reference).
/// </summary>
public static class WorkProjectSelector
{
    public static string? SelectWorkProjectPath(
        IReadOnlyList<OpenDocumentInfo> openDocuments,
        string? referencePath)
    {
        foreach (var doc in openDocuments)
        {
            if (doc.IsFamilyDocument || doc.IsLinked || doc.IsMiniProject) continue;
            if (string.IsNullOrEmpty(doc.PathName)) continue;
            if (!string.IsNullOrEmpty(referencePath)
                && string.Equals(doc.PathName, referencePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return doc.PathName;
        }
        return null;
    }
}
