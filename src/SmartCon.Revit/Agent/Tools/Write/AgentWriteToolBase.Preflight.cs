using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>Plan preflight: resolves ids and validates reference kinds before a plan starts.</summary>
public abstract partial class AgentWriteToolBase
{
    public async Task<PlanPreflightResult> PreflightAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expectedDocumentId = context.DocumentId;
        return await context.Revit
            .RaiseAsync(_ => PreflightOnRevitThread(expectedDocumentId, args, ct), ct)
            .ConfigureAwait(false);
    }

    private PlanPreflightResult PreflightOnRevitThread(
        string expectedDocumentId,
        JsonElement args,
        CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            var document = _revitContext.TryGetDocument();
            if (document is null)
                return PlanPreflightResult.Fail(
                    "no_active_document", "No Revit project document is open.");

            if (!string.IsNullOrEmpty(expectedDocumentId)
                && !string.Equals(
                    AgentDocumentIdentity.GetDocumentId(document),
                    expectedDocumentId,
                    StringComparison.Ordinal))
            {
                return PlanPreflightResult.Fail(
                    "document_changed",
                    "The active document changed before the plan started.");
            }

            if (document.IsReadOnly)
                return PlanPreflightResult.Fail(
                    "document_not_modifiable",
                    "The document is read-only, so a write plan cannot be applied.");

            return PreflightCore(document, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PlanPreflightResult.Fail(
                "preflight_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Default check: every ElementId-like argument must resolve, and view /
    /// level / template arguments must point at the right element class.
    /// Override for tool-specific checks (family compatibility, limits).
    /// </summary>
    protected virtual PlanPreflightResult PreflightCore(
        Document document,
        JsonElement args,
        CancellationToken ct)
    {
        foreach (var reference in EnumerateIdReferences(args))
        {
            var element = document.GetElement(ElementIdCompat.Create(reference.Id));
            if (element is null)
            {
                return PlanPreflightResult.Fail(
                    "unresolved_id",
                    "Element " + reference.Id + " (" + reference.Key + ") does not exist.");
            }

            var problem = ValidateReferenceKind(element, reference.Key);
            if (problem is not null)
                return PlanPreflightResult.Fail("invalid_reference", problem);
        }

        return PlanPreflightResult.Pass();
    }

    private static string? ValidateReferenceKind(Element element, string key)
    {
        var normalized = key.ToLowerInvariant();
        if (normalized.EndsWith("viewid", StringComparison.Ordinal)
            || normalized.EndsWith("viewids", StringComparison.Ordinal))
        {
            return element is View
                ? null
                : "Element " + element.Id.GetValue() + " (" + key + ") is not a view.";
        }

        if (normalized.EndsWith("levelid", StringComparison.Ordinal)
            || normalized.EndsWith("levelids", StringComparison.Ordinal))
        {
            return element is Level
                ? null
                : "Element " + element.Id.GetValue() + " (" + key + ") is not a level.";
        }

        if (normalized.EndsWith("templateid", StringComparison.Ordinal)
            || normalized.EndsWith("templateids", StringComparison.Ordinal))
        {
            return element is View view && view.IsTemplate
                ? null
                : "Element " + element.Id.GetValue() + " (" + key + ") is not a view template.";
        }

        return null;
    }

    private static IEnumerable<(string Key, long Id)> EnumerateIdReferences(JsonElement args) =>
        AgentJsonIds.Scan(args);
}
