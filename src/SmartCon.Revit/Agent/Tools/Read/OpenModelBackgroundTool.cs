using System.Linq;
using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Opens another model invisibly in the background (case 9 v2 multi-model
/// reads: «посмотри, как сделано в модели-эталоне, и повтори в моей»).
/// <see cref="Application.OpenDocumentFile"/> opens the document at the DB
/// level without any UI frame: the active document and the view the user sees
/// are not touched, no OnDocumentOpen-style UI noise happens. The returned
/// documentId is then accepted by every read tool through the optional
/// <c>documentId</c> argument (see <see cref="AgentReadToolBase"/>). The model
/// itself is never modified — read-only donor (owner decision 2026-11-22).
/// </summary>
public sealed class OpenModelBackgroundTool : IAgentTool
{
    private readonly IRevitContext _revitContext;

    public OpenModelBackgroundTool(IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "open_model_background",
        Description =
            "Opens a .rvt model invisibly in the background (no UI, the active document " +
            "stays active) and returns its documentId. Pass that documentId to any read " +
            "tool (list_views, query_elements, get_element_details...) through their " +
            "optional documentId argument to read the donor model instead of the active " +
            "one. The opened model is a read-only donor: nothing is written to it and it " +
            "is never saved; close it with close_model when done. Expensive: only open a " +
            "model when the user explicitly named its path.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Category = "Read",
        TimeoutMs = 300000,
        InputSchemaJson =
            @"{""type"":""object"",""properties"":{""path"":{""type"":""string""," +
            @"""description"":""Absolute path to the .rvt file to open in the background""}}," +
            @"""required"":[""path""],""additionalProperties"":false}"
    };

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return context.Revit.RaiseAsync(
            app => OpenOnRevitThread(app, args),
            ct);
    }

    internal ToolResult OpenOnRevitThread(object app, JsonElement args)
    {
        var path = AgentJson.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "path is required: absolute path to the .rvt file to open in the background.");
        }

        path = path!.Trim().Trim('"');
        if (!Path.IsPathRooted(path))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "path must be absolute (got '" + path + "'). Ask the user for the full " +
                "path to the model file.");
        }

        if (!File.Exists(path))
        {
            return ToolResult.Fail(
                "file_not_found",
                "File not found: " + path + ". Check the path with the user; do not scan " +
                "the disk on your own.");
        }

        var application = ResolveApplication(app);
        if (application is null)
        {
            return ToolResult.Fail(
                "tool_failed",
                "Revit Application is not available in this context; the model cannot be " +
                "opened in the background.");
        }

        var activeDocument = _revitContext.TryGetDocument();

        // "Already open" guard: opening the same file twice in one session is
        // both wasteful and risky (workshared central/local conflicts). Match
        // by PathName, case-insensitive (EmbeddedContentVerifier pattern).
        foreach (Document openDoc in application.Documents)
        {
            if (string.IsNullOrEmpty(openDoc.PathName)
                || !string.Equals(openDoc.PathName, path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = AgentDocumentIdentity.GetDocumentId(openDoc);
            AgentBackgroundDocuments.Register(id, openDoc, openDoc.PathName);

            // Distinct managed wrappers for one open document are normal
            // (application.Documents vs the context instance): compare the
            // open-document identity, not the .NET reference.
            var isActive = AgentDocumentIdentity.IsSameOpenDocument(openDoc, activeDocument);
            SmartConLogger.Info(
                "open_model_background: already open '" + Path.GetFileName(path) +
                "' (documentId registered, isActive=" + isActive + ")");

            var payload = AgentJson.Serialize(new
            {
                documentId = id,
                title = openDoc.Title,
                path = openDoc.PathName,
                isOpen = true,
                wasAlreadyOpen = true,
                isActive,
                isReadOnly = SafeFlag(openDoc, doc => doc.IsReadOnly),
                isWorkshared = SafeFlag(openDoc, doc => doc.IsWorkshared),
                elementCountPreview = SafeCount(openDoc),
                viewsPreview = BuildViewsPreview(openDoc),
                familiesPreview = BuildFamiliesPreview(openDoc),
                hint = "To read THIS model, pass documentId in the call, e.g. list_views({fields:[viewId,name],documentId:'" + id + "'}) — without it every read returns the ACTIVE document."
            });

            return ToolResult.Ok(
                payload,
                "Model is already open in this Revit session; the existing document was " +
                "registered for background reads.",
                warnings: isActive
                    ? new[] { AlreadyActiveWarning }
                    : null);
        }

        Document? document;
        using (var measure = SmartConLogger.Measure("OpenModelBackground"))
        {
            try
            {
                document = application.OpenDocumentFile(path);
            }
            catch (Autodesk.Revit.Exceptions.FileNotFoundException)
            {
                return ToolResult.Fail("file_not_found", "File not found: " + path + ".");
            }
            catch (Autodesk.Revit.Exceptions.CannotOpenBothCentralAndLocalException ex)
            {
                SmartConLogger.Warn(
                    "open_model_background rejected '" + Path.GetFileName(path) + "': " +
                    ex.GetType().Name + ": " + ex.Message +
                    " [Action: close either the central or the local copy in Revit and retry]");
                return ToolResult.Fail(
                    "open_conflict",
                    "Both the central and the local model cannot be open in the same Revit " +
                    "session (" + ex.Message + ")");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                SmartConLogger.Warn(
                    "open_model_background failed '" + Path.GetFileName(path) + "': " +
                    ex.GetType().Name + ": " + ex.Message +
                    " [Action: check the file version/format; if it must be upgraded, open it manually in Revit UI once]");
                return ToolResult.Fail(
                    "tool_failed",
                    "Revit refused to open the model: " + ex.GetType().Name + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    "open_model_background failed '" + Path.GetFileName(path) + "': " +
                    ex.GetType().Name + ": " + ex.Message +
                    " [Action: verify the path and file integrity with the user]");
                return ToolResult.Fail(
                    "tool_failed",
                    ex.GetType().Name + ": " + ex.Message);
            }

            if (measure.GetElapsedMilliseconds() > 5000)
            {
                SmartConLogger.Warn(
                    "open_model_background slow: " + (long)measure.GetElapsedMilliseconds() +
                    "ms for '" + Path.GetFileName(path) + "'" +
                    " [Action: large donor model — keep background read tours narrow and close it early]");
            }
        }

        if (document is null)
        {
            return ToolResult.Fail(
                "tool_failed",
                "OpenDocumentFile returned null for '" + path + "'.");
        }

        var documentId = AgentDocumentIdentity.GetDocumentId(document);
        AgentBackgroundDocuments.Register(documentId, document, document.PathName);

        SmartConLogger.Info(
            "open_model_background: opened '" + Path.GetFileName(path) +
            "' (documentId=" + ShortId(documentId) + ")");

        var result = AgentJson.Serialize(new
        {
            documentId,
            title = document.Title,
            path = document.PathName,
            isOpen = true,
            wasAlreadyOpen = false,
            isActive = false,
            isReadOnly = SafeFlag(document, doc => doc.IsReadOnly),
            isWorkshared = SafeFlag(document, doc => doc.IsWorkshared),
            elementCountPreview = SafeCount(document),
            viewsPreview = BuildViewsPreview(document),
            familiesPreview = BuildFamiliesPreview(document),
            hint = "To read THIS model, pass documentId in the call, e.g. list_views({fields:[viewId,name],documentId:'" + documentId + "'}) — without it every read returns the ACTIVE document."
        });

        return ToolResult.Ok(
            result,
            "Model opened in the background (invisible, active document unchanged). Pass " +
            "documentId to read tools to inspect the donor model; close it with " +
            "close_model when done.");
    }

    internal const string AlreadyActiveWarning =
        "The requested model IS the currently active document: it was not re-opened, " +
        "and reads with this documentId address the user's active session document. " +
        "Write tools against it keep their normal confirmation flow.";

    /// <summary>
    /// The RaiseAsync callback object is a UIApplication in production (the
    /// ExternalEvent passes it to ProcessQueue) and a bare
    /// <see cref="Application"/> in test harnesses; the active document's
    /// Application is the last-resort fallback (zero-document harnesses).
    /// </summary>
    private Application? ResolveApplication(object app)
    {
        // No direct UIApplication cast: JIT of RevitAPIUI types inside the
        // integration-test host crashes the session (mixed-mode init). The
        // production callback carries a UIApplication; read its Application
        // through reflection instead.
        if (app is Application application)
            return application;

        var property = app.GetType().GetProperty("Application");
        if (property?.GetValue(app) is Application owned)
            return owned;

        return _revitContext.TryGetDocument()?.Application;
    }

    private static bool SafeFlag(Document doc, Func<Document, bool> read)
    {
        try
        {
            return read(doc);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// First-look preview (#477): the most common reason to open a donor is to
    /// see how it is organised, so the open result carries the view names and
    /// the placed families with a best-effort "mark"-like shared value. This
    /// keeps the model from needing documentId routing for the basic look.
    /// </summary>
    private static object BuildViewsPreview(Document doc)
    {
        try
        {
            var names = new List<string>();
            foreach (var view in new FilteredElementCollector(doc)
                         .OfClass(typeof(View))
                         .Cast<View>()
                         .Where(view => !view.IsTemplate && view.ViewType != ViewType.Undefined && !(view is ViewSheet))
                         .Take(20))
            {
                names.Add(view.Name);
            }

            return names;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static object BuildFamiliesPreview(Document doc)
    {
        try
        {
            var rows = new List<object>();
            foreach (var family in new FilteredElementCollector(doc)
                         .OfClass(typeof(Autodesk.Revit.DB.Family))
                         .Cast<Autodesk.Revit.DB.Family>()
                         .Where(family => family.GetFamilySymbolIds().Count > 0)
                         .Take(15))
            {
                string? mark = null;
                var typeId = family.GetFamilySymbolIds().First();
                if (doc.GetElement(typeId) is ElementType symbol)
                {
                    foreach (Parameter parameter in symbol.Parameters)
                    {
                        if (!parameter.IsShared || !parameter.HasValue)
                            continue;

                        var definitionName = parameter.Definition?.Name ?? string.Empty;
                        if (definitionName.IndexOf("марка", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            definitionName.IndexOf("mark", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            mark = parameter.AsString();
                            break;
                        }
                    }
                }

                rows.Add(new
                {
                    family = family.Name,
                    type = (doc.GetElement(typeId) as ElementType)?.Name,
                    mark
                });
            }

            return rows;
        }
        catch (Exception)
        {
            return Array.Empty<object>();
        }
    }

    private static int? SafeCount(Document doc)
    {
        try
        {
            return new FilteredElementCollector(doc).GetElementCount();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ShortId(string documentId) =>
        documentId.Length <= 24 ? documentId : documentId.Substring(0, 24) + "…";
}
