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

            if (document.IsWorkshared)
                return PlanPreflightResult.Fail(
                    "workshared_not_supported",
                    "The active document is a workshared model.");

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

    private static IEnumerable<(string Key, long Id)> EnumerateIdReferences(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in args.EnumerateObject())
        {
            foreach (var reference in Enumerate(property.Name, property.Value))
                yield return reference;
        }

        static IEnumerable<(string Key, long Id)> Enumerate(string key, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id))
            {
                if (IsIdKey(key) && id > 0)
                    yield return (key, id);
                yield break;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    foreach (var reference in Enumerate(key, item))
                        yield return reference;
                }

                yield break;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    foreach (var reference in Enumerate(property.Name, property.Value))
                        yield return reference;
                }
            }
        }

        static bool IsIdKey(string key)
        {
            var normalized = key.ToLowerInvariant();
            if (normalized.EndsWith("categoryid", StringComparison.Ordinal)
                || normalized.EndsWith("categoryids", StringComparison.Ordinal)
                || normalized.EndsWith("worksetid", StringComparison.Ordinal)
                || normalized.EndsWith("worksetids", StringComparison.Ordinal)
                || normalized.EndsWith("parameterid", StringComparison.Ordinal)
                || normalized.EndsWith("parameterids", StringComparison.Ordinal))
            {
                // A parameter id (including negative built-in ids) is not an element id.
                return false;
            }

            return normalized.EndsWith("id", StringComparison.Ordinal)
                   || normalized.EndsWith("ids", StringComparison.Ordinal);
        }
    }
}
