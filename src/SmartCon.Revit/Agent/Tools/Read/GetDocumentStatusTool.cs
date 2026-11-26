using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Read tool: can the agent write to THIS document right now, and what can get
/// in the way. Read tools run outside any transaction, so the raw Revit flags
/// need their meaning spelled out: <c>Document.IsModifiable</c> is true only
/// while a transaction is open in the document (false is the normal idle
/// state), while <c>Document.IsReadOnly</c> is the flag that actually forbids
/// changes. Opens no transaction, changes nothing.
/// </summary>
public sealed class GetDocumentStatusTool : AgentReadToolBase
{
    private const int MaxListedFamilyDocuments = 20;
    private const int MaxListedWorksets = 20;

    public GetDocumentStatusTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_document_status",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Reports whether the agent can write to the document right now and what can block " +
            "it. Returns the Revit flags with their meaning: isReadOnly (true forbids changes " +
            "and even a new transaction), isModifiable (true ONLY while a transaction is open " +
            "in the document; false is the normal idle state), isReadOnlyFile (opened from a " +
            "read-only file: changes are possible but cannot be saved to the original file), " +
            "isModified; whether the document is a project or a family document (Family " +
            "Editor), workshared or detached, the active workset and who owns it, and whether " +
            "it has ever been saved. Also lists the documents open in this Revit session " +
            "(projects and family documents). writable=true means the document is free: not " +
            "read-only and no transaction open. In a workshared local copy writes are allowed: " +
            "elements owned by other users or out of date are skipped per item. Use it after a " +
            "write tool failed with document_not_modifiable, permission_denied, " +
            "central_unavailable, not_a_project_document or family_open_conflict, and before a " +
            "long write series. Read-only: opens no transaction and changes nothing.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJsonLast + @"},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "AgentTool",
            ("Method", nameof(GetDocumentStatusTool.Execute)),
            ("Document", SafeTitle(doc)));

        var flags = new RevitFlags
        {
            IsReadOnly = Safe(() => doc.IsReadOnly),
            IsModifiable = Safe(() => doc.IsModifiable),
            IsReadOnlyFile = Safe(() => doc.IsReadOnlyFile),
            IsModified = Safe(() => doc.IsModified),
            IsFamilyDocument = Safe(() => doc.IsFamilyDocument),
            IsWorkshared = Safe(() => doc.IsWorkshared),
            IsDetached = Safe(() => doc.IsWorkshared && doc.IsDetached)
        };

        var path = Safe(() => doc.PathName) ?? string.Empty;
        var isSaved = !string.IsNullOrEmpty(path);
        var open = ReadOpenDocuments(doc, ct);

        var blockers = new List<object>();
        var codes = new List<string>();
        string? firstMessage = null;
        string? firstAction = null;

        if (flags.IsReadOnly == true)
        {
            Add("document_read_only", "any",
                "Document.IsReadOnly is true: the model cannot be modified and no transaction " +
                "can be started right now (Revit is processing failures, or the command runs " +
                "in read-only mode).",
                "Wait until the current operation finishes, or reopen the document normally.");
        }

        if (flags.IsModifiable == true)
        {
            Add("transaction_open", "any",
                "Document.IsModifiable is true, which happens only while a transaction is " +
                "open in the document: the agent's write tools cannot start their own " +
                "transaction until it closes.",
                "Wait for the running operation or job to finish, then retry.");
        }

        if (flags.IsFamilyDocument == true)
        {
            Add("family_document", "project_tools",
                "The document is a family document, i.e. the Family Editor is the active " +
                "window: project write tools refuse with not_a_project_document.",
                "Load the family back into the project, make the project the active document " +
                "and retry.");
        }

        if (flags.IsReadOnlyFile == true)
        {
            Add("read_only_file", "save",
                "The document was opened from a read-only file (another user has it open, or " +
                "the file has the read-only attribute): changes can be made but cannot be " +
                "saved to the original file.",
                "Save the document under a different name in Revit (Save As) before relying " +
                "on the changes.");
        }

        if (!isSaved)
        {
            Add("unsaved_document", "save",
                "The document has never been saved: it has no file path.",
                "Save it in Revit (Save As) first; save_document reports " +
                "document_not_saved_before.");
        }

        if (open.OtherFamilies > 0)
        {
            Add("family_documents_open", "family_tools",
                open.OtherFamilies + " family document(s) are open in this Revit session. " +
                "Family write tools open the family document themselves (EditFamily) and " +
                "refuse while a family is open in the Family Editor (family_open_conflict).",
                "Close the Family Editor (load the family back into the project) and retry " +
                "the family write tools.");
        }

        var flagsKnown = flags.IsReadOnly is not null && flags.IsModifiable is not null;
        var writable = flagsKnown ? flags.IsReadOnly != true && flags.IsModifiable != true : (bool?)null;
        var state = !flagsKnown
            ? "unknown"
            : flags.IsReadOnly == true
                ? "read_only"
                : flags.IsModifiable == true
                    ? "transaction_open"
                    : "writable";
        var agentWriteTools = writable == true
                              && flags.IsFamilyDocument != true
            ? "supported"
            : "blocked";

        var payload = new
        {
            // Not "document": that top-level key is reserved for the routing
            // fact of AgentReadToolBase (C9-02), and a tool's own field would
            // silently suppress it.
            documentInfo = new
            {
                title = SafeTitle(doc),
                path = isSaved ? path : null,
                isSaved,
                revitVersion = Safe(() => doc.Application.VersionNumber),
                isFamilyDocument = flags.IsFamilyDocument,
                familyName = flags.IsFamilyDocument == true ? FamilyName(doc) : null,
                isWorkshared = flags.IsWorkshared,
                isDetached = flags.IsWorkshared == true ? flags.IsDetached : null
            },
            revitFlags = new
            {
                isReadOnly = flags.IsReadOnly,
                isModifiable = flags.IsModifiable,
                isReadOnlyFile = flags.IsReadOnlyFile,
                isModified = flags.IsModified
            },
            writable,
            state,
            agentWriteTools,
            blockerCodes = codes,
            blockers,
            session = new
            {
                openDocuments = open.Total,
                projectDocuments = open.Total - open.Families,
                familyDocuments = open.Families,
                familyDocumentsOpen = open.Rows,
                familyDocumentsTruncated = open.OtherFamilies > open.Rows.Count ? open.OtherFamilies : (int?)null
            },
            worksharing = flags.IsWorkshared == true ? ReadWorksharing(doc) : null,
            worksharingNote = flags.IsWorkshared != true
                ? null
                : flags.IsDetached == true
                    ? "Detached workshared copy: there is no central to borrow from, edits are " +
                      "local and allowed."
                    : "Workshared local copy: free elements are written; an element owned by " +
                      "another user, an out-of-date element and a non-editable active workset " +
                      "are skipped per item and can be retried after relinquish/Reload Latest.",
            hint = "isModifiable is true only while a transaction is open in the document; " +
                   "false outside a transaction is the normal state and does not mean the " +
                   "document is locked. Family documents listed in the session may be open in " +
                   "the Family Editor or only in memory - the Revit DB API does not tell them " +
                   "apart; the Family Editor is active when the document itself is a family " +
                   "document."
        };

        var message = state == "writable"
            ? "Document '" + SafeTitle(doc) + "' is writable: not read-only, no transaction open. " +
              SessionSummary(open)
            : "Document '" + SafeTitle(doc) + "' cannot be modified by the agent's write tools " +
              "now (" + state + ").";
        if (blockers.Count > 0)
        {
            message += " Blockers (" + blockers.Count + "): " + string.Join("; ", codes) +
                       ". First: " + firstMessage + " [Action: " + firstAction + "]";
        }

        SmartConLogger.Debug(
            "get_document_status: state=" + state + ", agentWriteTools=" + agentWriteTools +
            ", blockers=" + (codes.Count == 0 ? "none" : string.Join(",", codes)));

        return ToolResult.Ok(AgentJson.Serialize(payload), message);

        void Add(string code, string scope, string message, string action)
        {
            codes.Add(code);
            blockers.Add(new { code, scope, message, action });
            firstMessage ??= message;
            firstAction ??= action;
        }
    }

    private static string SessionSummary(OpenDocuments open) =>
        open.Total + " document(s) open in this Revit session: " +
        (open.Total - open.Families) + " project, " + open.Families + " family.";

    private static OpenDocuments ReadOpenDocuments(Document requested, CancellationToken ct)
    {
        var open = new OpenDocuments();
        try
        {
            var requestedId = AgentDocumentIdentity.GetDocumentId(requested);
            foreach (Document candidate in requested.Application.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (candidate is null)
                    continue;

                open.Total++;
                var isFamily = Safe(() => candidate.IsFamilyDocument) == true;
                if (isFamily)
                    open.Families++;

                var isRequested = AgentDocumentIdentity.IsSameOpenDocument(candidate, requested)
                                  || string.Equals(
                                      Safe(() => AgentDocumentIdentity.GetDocumentId(candidate)),
                                      requestedId,
                                      StringComparison.Ordinal);
                if (isRequested || !isFamily)
                    continue;

                open.OtherFamilies++;
                if (open.Rows.Count >= MaxListedFamilyDocuments)
                    continue;

                open.Rows.Add(new OpenDocumentRow
                {
                    Title = SafeTitle(candidate),
                    IsModified = Safe(() => candidate.IsModified),
                    IsReadOnly = Safe(() => candidate.IsReadOnly),
                    Path = EmptyAsNull(Safe(() => candidate.PathName))
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "get_document_status could not list the open documents: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: the status of the requested document is still reported; only the " +
                "session list is incomplete]");
        }

        return open;
    }

    /// <summary>
    /// Worksharing facts of a workshared document; on a plain document Revit
    /// has no such data and the method is not called.
    /// </summary>
    private static object ReadWorksharing(Document doc)
    {
        try
        {
            string? centralPath = null;
            try
            {
                var central = doc.GetWorksharingCentralModelPath();
                if (central is not null)
                    centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
            }
            catch (Exception ex)
            {
                centralPath = "unavailable: " + ex.GetType().Name + ": " + ex.Message;
            }

            object? activeWorkset = null;
            var userWorksets = 0;
            var notEditableTotal = 0;
            var notEditable = new List<object>();
            using (var table = doc.GetWorksetTable())
            {
                var active = table.GetWorkset(table.GetActiveWorksetId());
                if (active is not null)
                {
                    activeWorkset = new
                    {
                        name = active.Name,
                        isEditable = active.IsEditable,
                        owner = EmptyAsNull(active.Owner),
                        isOpen = active.IsOpen
                    };
                }

                foreach (Workset workset in new FilteredWorksetCollector(doc)
                             .OfKind(WorksetKind.UserWorkset))
                {
                    userWorksets++;
                    if (workset.IsEditable)
                        continue;

                    notEditableTotal++;
                    if (notEditable.Count >= MaxListedWorksets)
                        continue;

                    notEditable.Add(new { name = workset.Name, owner = EmptyAsNull(workset.Owner) });
                }
            }

            return new
            {
                centralPath,
                activeWorkset,
                userWorksets,
                notEditableWorksets = notEditable,
                notEditableTotal,
                notEditableTruncated = notEditableTotal > notEditable.Count,
                note = "IsEditable is true for a workset owned by the current user. Writes are " +
                       "allowed: an element owned by another user, an out-of-date element or a " +
                       "non-editable active workset is skipped per item (element_owned_by_other_user, " +
                       "element_out_of_date, workset_owned_by_other_user)."
            };
        }
        catch (Exception ex)
        {
            return new
            {
                unavailable = true,
                reason = ex.GetType().Name + ": " + ex.Message
            };
        }
    }

    private static string? FamilyName(Document familyDocument)
    {
        try
        {
            return familyDocument.OwnerFamily?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string SafeTitle(Document document)
    {
        try
        {
            return document.Title ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? EmptyAsNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static T? Safe<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class RevitFlags
    {
        public bool? IsReadOnly { get; set; }

        public bool? IsModifiable { get; set; }

        public bool? IsReadOnlyFile { get; set; }

        public bool? IsModified { get; set; }

        public bool? IsFamilyDocument { get; set; }

        public bool? IsWorkshared { get; set; }

        public bool? IsDetached { get; set; }
    }

    /// <summary>
    /// Open documents of the session. <c>OtherFamilies</c> counts every family
    /// document except the requested one; <c>Rows</c> lists at most
    /// <see cref="MaxListedFamilyDocuments"/> of them.
    /// </summary>
    private sealed class OpenDocuments
    {
        public int Total { get; set; }

        public int Families { get; set; }

        public int OtherFamilies { get; set; }

        public List<OpenDocumentRow> Rows { get; } = new();
    }

    private sealed class OpenDocumentRow
    {
        public string Title { get; set; } = string.Empty;

        public string? Path { get; set; }

        public bool? IsModified { get; set; }

        public bool? IsReadOnly { get; set; }
    }
}
