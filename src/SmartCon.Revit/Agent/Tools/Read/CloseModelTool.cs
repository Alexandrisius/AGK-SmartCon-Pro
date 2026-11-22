using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Closes a background model previously opened by open_model_background (or
/// any non-active document open in the session, resolved by path). Closing is
/// always without saving — the donor model is read-only by design, so
/// <c>Close(false)</c> discards nothing the agent authored. The close ritual
/// follows <see cref="AgentFamilyDocCloser"/> (#96 / REVIT-237190):
/// Close(false) → Marshal.ReleaseComObject → best-effort RevitBalloonNudge
/// that re-syncs the WPF render thread with the UI thread.
/// </summary>
public sealed class CloseModelTool : IAgentTool
{
    private readonly IRevitContext _revitContext;

    public CloseModelTool(IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "close_model",
        Description =
            "Closes a background model opened by open_model_background without saving " +
            "(the donor model is never modified). Accepts documentId (from " +
            "open_model_background) or path. The active document cannot be closed by the " +
            "agent — close it in the Revit UI. Close the donor as soon as the read tour " +
            "is finished: an open background model holds memory.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Category = "Read",
        TimeoutMs = 120000,
        InputSchemaJson =
            @"{""type"":""object"",""properties"":{" +
            @"""documentId"":{""type"":""string"",""description"":""documentId returned by open_model_background""}," +
            @"""path"":{""type"":""string"",""description"":""absolute path of the model to close (alternative to documentId)""}}," +
            @"""additionalProperties"":false}"
    };

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return context.Revit.RaiseAsync(
            app => CloseOnRevitThread(app, args),
            ct);
    }

    internal ToolResult CloseOnRevitThread(object app, JsonElement args)
    {
        var documentId = AgentJson.GetString(args, "documentId");
        var path = AgentJson.GetString(args, "path");

        if (string.IsNullOrWhiteSpace(documentId) && string.IsNullOrWhiteSpace(path))
        {
            var open = AgentBackgroundDocuments.ListAll();
            if (open.Count == 0)
            {
                return ToolResult.Fail(
                    "model_not_open",
                    "No background model is open. Call open_model_background(path=...) " +
                    "first.");
            }

            return ToolResult.Ok(
                AgentJson.Serialize(new
                {
                    openModels = open.Select(entry => new
                    {
                        documentId = entry.DocumentId,
                        path = entry.PathName
                    }).ToList()
                }),
                "documentId or path is required. Background models currently open: " +
                open.Count + ".");
        }

        if (!string.IsNullOrWhiteSpace(path))
            path = path!.Trim().Trim('"');

        var application = ResolveApplication(app);

        Document? document = null;
        var resolvedId = documentId;

        if (!string.IsNullOrWhiteSpace(documentId))
        {
            if (!AgentBackgroundDocuments.TryResolve(documentId!, out document))
            {
                // The document may be open in the session without being
                // registered (opened before close_model); find it by identity.
                document = FindOpenByIdentity(application, documentId!);
                if (document is null)
                {
                    return ToolResult.Fail(
                        "model_not_open",
                        "No open model matches documentId '" + documentId + "'. It was " +
                        "probably already closed. Call open_model_background again if you " +
                        "still need it.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            if (!AgentBackgroundDocuments.TryResolveByPath(path!, out resolvedId, out document))
            {
                document = FindOpenByPath(application, path!);
                if (document is null)
                {
                    return ToolResult.Fail(
                        "model_not_open",
                        "No open model matches path '" + path + "'. It was probably already " +
                        "closed.");
                }

                resolvedId = AgentDocumentIdentity.GetDocumentId(document);
            }
        }

        if (document is null)
        {
            return ToolResult.Fail(
                "model_not_open",
                "No open model matches the request.");
        }

        // Autodesk contract: Close throws InvalidOperationException on the
        // currently active document. Fail honestly instead of letting Revit
        // throw — the model must understand this is a UI-only operation.
        // Wrapper instances may differ (registry/scan vs context) — compare
        // the open-document identity, not the .NET reference.
        var activeDocument = _revitContext.TryGetDocument();
        if (AgentDocumentIdentity.IsSameOpenDocument(document, activeDocument))
        {
            return ToolResult.Fail(
                "model_is_active",
                "The requested model is the currently active document — the agent cannot " +
                "close it. The user closes active documents in the Revit UI " +
                "(File → Close). If you only meant to stop reading it, nothing else is " +
                "needed: reads against the active document do not hold it open.");
        }

        var title = SafeTitle(document);
        if (string.IsNullOrEmpty(resolvedId))
            resolvedId = AgentDocumentIdentity.GetDocumentId(document);

        // Close(false) + COM release + render-thread nudge — the AgentFamilyDocCloser
        // ritual (issues #96 / REVIT-237190). Every step is best-effort.
        var closed = false;
        try
        {
            closed = document.Close(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "close_model could not close '" + title + "': " + ex.GetType().Name + ": " +
                ex.Message + " [Action: the document stays open; it can be closed " +
                "manually in Revit — no data was saved or changed]");
            return ToolResult.Fail(
                "tool_failed",
                "Revit refused to close the model: " + ex.GetType().Name + ": " + ex.Message +
                ". The model stays open; nothing was saved or changed.");
        }

        AgentBackgroundDocuments.Unregister(resolvedId!);

        try
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(document);
        }
        catch (Exception)
        {
            // Managed wrapper, not a real COM object.
        }

        try
        {
            RevitBalloonNudge.Nudge(" ");
        }
        catch (Exception)
        {
            // Best-effort: right-click on the pane still recovers the UI.
        }

        SmartConLogger.Info(
            "close_model: closed '" + title + "' without saving (documentId unregistered)");

        return ToolResult.Ok(
            AgentJson.Serialize(new
            {
                documentId = resolvedId,
                title,
                closed = true,
                saved = false
            }),
            closed
                ? "Model '" + title + "' closed without saving."
                : "Model '" + title + "' close reported failure by Revit; no data was " +
                  "saved or changed.");
    }

    private Application? ResolveApplication(object app)
    {
        // No direct UIApplication cast: JIT of RevitAPIUI types inside the
        // integration-test host crashes the session (mixed-mode init).
        if (app is Application application)
            return application;

        var property = app.GetType().GetProperty("Application");
        if (property?.GetValue(app) is Application owned)
            return owned;

        return _revitContext.TryGetDocument()?.Application;
    }

    private static Document? FindOpenByIdentity(Application? application, string documentId)
    {
        if (application is null)
            return null;

        foreach (Document openDoc in application.Documents)
        {
            try
            {
                if (string.Equals(
                        AgentDocumentIdentity.GetDocumentId(openDoc),
                        documentId,
                        StringComparison.Ordinal))
                {
                    return openDoc;
                }
            }
            catch (Exception)
            {
                // Unsaved documents have no stable identity — skip.
            }
        }

        return null;
    }

    private static Document? FindOpenByPath(Application? application, string path)
    {
        if (application is null)
            return null;

        foreach (Document openDoc in application.Documents)
        {
            if (string.IsNullOrEmpty(openDoc.PathName))
                continue;

            if (string.Equals(openDoc.PathName, path, StringComparison.OrdinalIgnoreCase))
                return openDoc;
        }

        return null;
    }

    private static string SafeTitle(Document document)
    {
        try
        {
            return document.Title;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
