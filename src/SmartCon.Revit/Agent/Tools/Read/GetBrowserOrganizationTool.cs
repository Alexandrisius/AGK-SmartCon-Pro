using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Reads the project-browser organization: every saved organization for the
/// target (views or sheets), which one is current, its sorting parameter and
/// the folder path of a sample element. The Revit API is read-only here —
/// switching or creating organizations stays in the Browser Organization dialog.
/// </summary>
public sealed class GetBrowserOrganizationTool : AgentReadToolBase
{
    public GetBrowserOrganizationTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_browser_organization",
        Description =
            "Reads the project-browser organization: all saved organizations for views or " +
            "sheets (name, id, isCurrent), the current one, its sorting parameter and " +
            "order, and the folder path a sample element gets (the grouping levels with " +
            "their parameters — the levels set_browser_folder writes into). Use it before " +
            "set_browser_folder to learn the writable grouping parameter. Switching or " +
            "creating organizations is not exposed by the Revit API (read-only since " +
            "2016, still read-only in 2026) — reorganizing requires the Browser " +
            "Organization dialog in the Revit UI.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""target"":{""type"":""string"",""enum"":[""views"",""sheets""],""default"":""views""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var target = AgentJson.GetString(args, "target") ?? "views";
        var wantSheets = string.Equals(target, "sheets", StringComparison.OrdinalIgnoreCase);
        if (!wantSheets && !string.Equals(target, "views", StringComparison.OrdinalIgnoreCase))
        {
            return ToolResult.Fail(
                "unknown_target",
                "Unknown target '" + target + "'. Allowed: views, sheets.");
        }

        var organizationType = wantSheets ? BrowserOrganizationType.Sheets : BrowserOrganizationType.Views;
        var current = wantSheets
            ? BrowserOrganization.GetCurrentBrowserOrganizationForSheets(doc)
            : BrowserOrganization.GetCurrentBrowserOrganizationForViews(doc);

        var organizations = new List<(long Id, string Name, bool IsCurrent)>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(BrowserOrganization)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not BrowserOrganization organization)
                continue;

            try
            {
                if (organization.Type != organizationType)
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            organizations.Add((
                organization.Id.GetValue(),
                organization.Name ?? string.Empty,
                current is not null && organization.Id == current.Id));
        }

        organizations.Sort((left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : left.Id.CompareTo(right.Id);
        });

        var organizationCards = organizations
            .Select(entry => new { id = entry.Id, name = entry.Name, isCurrent = entry.IsCurrent })
            .ToList();

        object? currentDetails = null;
        if (current is not null)
            currentDetails = DescribeCurrent(doc, current, wantSheets);

        var payload = new
        {
            target = wantSheets ? "sheets" : "views",
            current = current is null ? null : new
            {
                id = current.Id.GetValue(),
                name = current.Name
            },
            organizations = organizationCards,
            currentDetails,
            writableByApi = false,
            note =
                "Read-only: the Revit API cannot switch the current organization or create " +
                "one (no setter, no builder; grouped-by rules are not exposed). Use the " +
                "Browser Organization dialog (View tab) to switch or create; set_browser_folder " +
                "writes the grouping parameter values inside the current organization."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            organizations.Count + " organization(s) for " + (wantSheets ? "sheets" : "views") +
            "; current: " + (current?.Name ?? "none") + ".");
    }

    private static object DescribeCurrent(
        Document doc,
        BrowserOrganization organization,
        bool wantSheets)
    {
        string? sortingParameter = null;
        try
        {
#if REVIT2026_OR_GREATER
            var sortingPath = organization.GetSortingParameterIdPath();
            sortingParameter = sortingPath is null || sortingPath.Count == 0
                ? null
                : ParameterNameById(doc, sortingPath[sortingPath.Count - 1]);
#else
            sortingParameter = ParameterNameById(doc, organization.SortingParameterId);
#endif
        }
        catch (Exception)
        {
            // Sorting by a built-in property without a readable parameter name.
        }

        string? sortingOrder = null;
        try
        {
            sortingOrder = organization.SortingOrder.ToString();
        }
        catch (Exception)
        {
            // Keep null.
        }

        return new
        {
            sortingParameter,
            sortingOrder,
            sampleFolders = SampleFolders(doc, organization, wantSheets)
        };
    }

    private static List<object> SampleFolders(
        Document doc,
        BrowserOrganization organization,
        bool wantSheets)
    {
        var folders = new List<object>();
        ElementId? sampleId = null;
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(View)))
        {
            if (element is not View view || view.IsTemplate)
                continue;

            if (wantSheets != (view is ViewSheet))
                continue;

            sampleId = view.Id;
            break;
        }

        if (sampleId is null)
            return folders;

        try
        {
            foreach (var item in organization.GetFolderItems(sampleId))
            {
                string? parameterName;
#if REVIT2026_OR_GREATER
                var path = item.GetGroupingParameterIdPath();
                parameterName = path is null || path.Count == 0
                    ? null
                    : ParameterNameById(doc, path[path.Count - 1]);
#else
                parameterName = item.ElementId is null ? null : ParameterNameById(doc, item.ElementId);
#endif
                folders.Add(new
                {
                    folder = item.Name,
                    parameter = parameterName
                });
            }
        }
        catch (Exception)
        {
            // A document without browser folders for this element: return what we have.
        }

        return folders;
    }

    internal static string? ParameterNameById(Document doc, ElementId? parameterId)
    {
        if (parameterId is null)
            return null;

        var value = parameterId.GetValue();
        if (value < 0)
        {
            var builtIn = (BuiltInParameter)value;
            try
            {
                return LabelUtils.GetLabelFor(builtIn);
            }
            catch (Exception)
            {
                return builtIn.ToString();
            }
        }

        return doc.GetElement(parameterId)?.Name;
    }
}
