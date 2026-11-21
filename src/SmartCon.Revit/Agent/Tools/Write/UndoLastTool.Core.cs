using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Core helpers shared by every undo zone: the exception-safe revert wrapper,
/// the change-item report factory, element resolution (UniqueId first, then
/// ElementId) and the generic instance:/type: parameter restore.
/// </summary>
public sealed partial class UndoLastTool
{
    private static (bool Ok, string? ErrorCode, string? Reason) RevertSafely(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        try
        {
            return Revert(doc, element, item, force);
        }
        catch (Exception ex)
        {
            return (false, "revert_failed", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static WriteItemReport CreateReport(ChangeItem item) => new()
    {
        Id = item.ElementId,
        Name = item.ParameterName,
        OldValue = item.NewValue,
        NewValue = item.OldValue
    };

    private static HashSet<long>? ParseIdSet(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var ids = JsonSerializer.Deserialize<long[]>(json!);
            return ids is null ? null : new HashSet<long>(ids);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (bool, string?, string?) RevertParameter(
        Document doc,
        Element element,
        ChangeItem item,
        bool force)
    {
        var key = item.ParameterName!;
        var typeScope = key.StartsWith("type:", StringComparison.Ordinal);
        var name = key.Substring(key.IndexOf(':') + 1);

        var resolution = AgentParameterWriteHelper.Resolve(
            doc,
            element,
            name,
            null,
            typeScope ? "type" : "instance");

        if (!resolution.Ok)
            return (false, resolution.ErrorCode, resolution.Reason);

        var parameter = resolution.Parameter!;
        var current = AgentParameterWriteHelper.RawValue(parameter);
        if (string.Equals(current, item.OldValue, StringComparison.Ordinal))
            return (false, null, "The parameter already matches the recorded previous value.");

        if (!force && !string.Equals(current, item.NewValue, StringComparison.Ordinal))
        {
            return (false, "undo_conflict",
                "Parameter '" + name + "' has the value '" + current +
                "' instead of '" + item.NewValue + "' — it was changed after the " +
                "operation (conflict).");
        }

        if (!AgentParameterWriteHelper.TrySet(
                parameter, item.OldValue, "raw", out var errorCode, out var reason))
        {
            return (false, errorCode, reason);
        }

        return (true, null, null);
    }

    private static Element? ResolveElement(Document doc, ChangeItem item)
    {
        if (!string.IsNullOrEmpty(item.UniqueId))
        {
            var byUniqueId = doc.GetElement(item.UniqueId);
            if (byUniqueId is not null)
                return byUniqueId;
        }

        return item.ElementId.HasValue
            ? doc.GetElement(ElementIdCompat.Create(item.ElementId.Value))
            : null;
    }

    private static string IdText(ElementId id) => id == ElementId.InvalidElementId
        ? string.Empty
        : id.GetValue().ToString(CultureInfo.InvariantCulture);
}
