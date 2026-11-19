using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Resolves a select filter into view ids so write tools do not require the
/// model to enumerate nine view ids by hand. Supported filters: type/viewType,
/// levelId, folder (browser folder substring), nameContains and templateId.
/// View templates are always excluded.
/// </summary>
internal static class AgentViewSelector
{
    internal static IReadOnlyList<long> Resolve(
        Document doc,
        JsonElement select,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        if (select.ValueKind != JsonValueKind.Object)
        {
            errorCode = "invalid_arguments";
            message = "select must be an object with at least one filter.";
            return Array.Empty<long>();
        }

        var levelId = AgentJson.GetLong(select, "levelId", 0);
        var templateId = AgentJson.GetLong(select, "templateId", 0);
        var nameContains = AgentJson.GetString(select, "nameContains");
        var folder = AgentJson.GetString(select, "folder");
        var type = AgentJson.GetString(select, "type") ?? AgentJson.GetString(select, "viewType");

        if (levelId <= 0
            && templateId <= 0
            && string.IsNullOrWhiteSpace(nameContains)
            && string.IsNullOrWhiteSpace(folder)
            && string.IsNullOrWhiteSpace(type))
        {
            errorCode = "invalid_arguments";
            message = "select needs at least one of type, levelId, folder, nameContains, templateId.";
            return Array.Empty<long>();
        }

        string? typeName = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            foreach (var candidate in Enum.GetNames(typeof(ViewType)))
            {
                if (string.Equals(candidate, type, StringComparison.OrdinalIgnoreCase))
                {
                    typeName = candidate;
                    break;
                }
            }

            if (typeName is null)
            {
                errorCode = "invalid_arguments";
                message = "Unknown view type '" + type + "'. Allowed: " +
                          string.Join(", ", Enum.GetNames(typeof(ViewType))) + ".";
                return Array.Empty<long>();
            }
        }

        BrowserOrganization? browserOrganization = null;
        try
        {
            browserOrganization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);
        }
        catch (Exception)
        {
            // Folder filtering is optional; a broken browser organization matches nothing.
        }

        var types = typeName is null ? Array.Empty<string>() : new[] { typeName };
        var levels = levelId > 0 ? new[] { levelId } : Array.Empty<long>();
        var folders = string.IsNullOrWhiteSpace(folder)
            ? Array.Empty<string>()
            : new[] { folder! };

        var result = new List<long>();
        foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
        {
            if (!ListViewsTool.IsSupportedView(view) || view.IsTemplate)
                continue;
            if (types.Length > 0 && !ListViewsTool.MatchesType(view, types))
                continue;
            if (levels.Length > 0 && !ListViewsTool.MatchesLevel(view, levels))
                continue;
            if (!ListViewsTool.MatchesName(view, nameContains))
                continue;
            if (templateId > 0
                && (view.ViewTemplateId is null
                    || view.ViewTemplateId.GetValue() != templateId))
            {
                continue;
            }

            if (folders.Length > 0)
            {
                var card = ListViewsTool.CreateCard(doc, view, browserOrganization);
                if (!ListViewsTool.MatchesFolder(card, folders))
                    continue;
            }

            result.Add(view.Id.GetValue());
        }

        result.Sort();
        if (result.Count == 0)
        {
            errorCode = "no_items";
            message = "The select filter matched no views. Check type, levelId, folder, " +
                      "nameContains and templateId.";
        }

        return result;
    }
}