using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>Typed selector, rename rules and strict key contract of rename_views.</summary>
public sealed partial class RenameViewsJobAction
{
    private static readonly JobSpecObjectRule[] SpecRules =
    {
        new()
        {
            Path = string.Empty,
            AllowedKeys = new[] { "select", "rename" }
        },
        new()
        {
            Path = "select",
            AllowedKeys = new[]
            {
                "viewIds", "elementIds", "nameContains", "nameStartsWith", "nameEndsWith",
                "viewTypes", "viewType", "types", "type", "levelIds", "folders", "folder",
                "templateName", "template", "templateId", "hasTemplate", "isTemplate", "includeTemplates",
                "excludeTemplates", "excludeSheets", "sheetNumber", "onSheet", "limit"
            }
        },
        new()
        {
            Path = "rename",
            AllowedKeys = new[]
            {
                "find", "replace", "prefix", "suffix", "setName", "numbering"
            }
        },
        new()
        {
            Path = "rename.numbering",
            AllowedKeys = new[] { "start", "step", "padding", "format" }
        }
    };

    private static bool MatchesSelect(
        View view,
        ViewsSelectSpec select,
        Document document)
    {
        var ids = select.ViewIds is { Count: > 0 } ? select.ViewIds : select.ElementIds;
        if (ids is { Count: > 0 } && !ids.Contains(view.Id.GetValue()))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(select.NameContains) &&
            view.Name.IndexOf(select.NameContains!, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(select.NameStartsWith) &&
            !view.Name.StartsWith(select.NameStartsWith!, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(select.NameEndsWith) &&
            !view.Name.EndsWith(select.NameEndsWith!, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requestedTypes = new List<string>();
        if (select.ViewTypes is { Count: > 0 })
        {
            requestedTypes.AddRange(select.ViewTypes);
        }

        if (select.Types is { Count: > 0 })
        {
            requestedTypes.AddRange(select.Types);
        }

        AddIfPresent(requestedTypes, select.ViewType);
        AddIfPresent(requestedTypes, select.Type);

        if (requestedTypes.Count > 0 && !ListViewsTool.MatchesType(view, requestedTypes))
        {
            return false;
        }

        if (select.LevelIds is { Count: > 0 } &&
            !ListViewsTool.MatchesLevel(view, select.LevelIds))
        {
            return false;
        }

        var templateId = view.ViewTemplateId;
        var hasTemplate = templateId != ElementId.InvalidElementId;
        if (select.HasTemplate is { } expectTemplate && hasTemplate != expectTemplate)
        {
            return false;
        }

        if (select.TemplateId is { } expectedTemplateId &&
            (!hasTemplate || templateId.GetValue() != expectedTemplateId))
        {
            return false;
        }

        var requestedTemplate = string.IsNullOrWhiteSpace(select.TemplateName)
            ? select.Template
            : select.TemplateName;
        if (!string.IsNullOrWhiteSpace(requestedTemplate))
        {
            var template = hasTemplate ? document.GetElement(templateId) : null;
            if (template is null ||
                !string.Equals(template.Name, requestedTemplate, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(select.SheetNumber))
        {
            var sheetNumber = view.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString();
            if (!string.Equals(sheetNumber, select.SheetNumber, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (select.OnSheet is { } expectOnSheet)
        {
            var sheetNumber = view.get_Parameter(BuiltInParameter.VIEWER_SHEET_NUMBER)?.AsString();
            var isOnSheet = !string.IsNullOrWhiteSpace(sheetNumber) &&
                            sheetNumber is not ("—" or "-" or "---");
            if (isOnSheet != expectOnSheet)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddIfPresent(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value!);
        }
    }

    private static string ComputeName(ViewsRenameSpec rename, string oldName, int index)
    {
        var numbering = rename.Numbering;
        var numberText = FormatNumber(numbering, index);
        var name = rename.SetName ?? oldName;

        if (!string.IsNullOrWhiteSpace(rename.SetName))
        {
            var containsNumber = name.IndexOf("{n}", StringComparison.Ordinal) >= 0;
            name = name
                .Replace("{name}", oldName)
                .Replace("{index}", (index + 1).ToString(CultureInfo.InvariantCulture))
                .Replace("{n}", numberText);

            // setName already owns the number placeholder: applying numbering.format
            // as well would produce "План_1_01" instead of "План_01".
            if (numbering is not null && !containsNumber)
            {
                name = ApplyNumberingFormat(numbering, name, numberText);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(rename.Find))
            {
                name = name.Replace(rename.Find!, rename.Replace ?? string.Empty);
            }

            if (!string.IsNullOrEmpty(rename.Prefix))
            {
                name = rename.Prefix + name;
            }

            if (!string.IsNullOrEmpty(rename.Suffix))
            {
                name = name + rename.Suffix;
            }

            if (numbering is not null)
            {
                name = ApplyNumberingFormat(numbering, name, numberText);
            }
        }

        return name.Trim();
    }

    private static string FormatNumber(NumberingSpec? numbering, int index)
    {
        var number = numbering is null
            ? index + 1
            : numbering.Start + index * (numbering.Step == 0 ? 1 : numbering.Step);
        var text = number.ToString(CultureInfo.InvariantCulture);
        return numbering is { Padding: > 0 }
            ? text.PadLeft(numbering.Padding, '0')
            : text;
    }

    private static string ApplyNumberingFormat(NumberingSpec numbering, string name, string numberText)
    {
        var format = string.IsNullOrWhiteSpace(numbering.Format) ? "{base}_{n}" : numbering.Format!;
        return format.Replace("{base}", name).Replace("{n}", numberText);
    }

    private static Dictionary<string, long> CollectViewNames(Document document)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in new FilteredElementCollector(document).OfClass(typeof(View)).Cast<View>())
        {
            map[view.Name] = view.Id.GetValue();
        }

        return map;
    }

    private sealed class RenameCandidate
    {
        public required View View { get; init; }

        public required string OldName { get; init; }

        public required string NewName { get; init; }

        public string? TempName { get; set; }

        public string? Error { get; set; }
    }

    public sealed class RenameSpec
    {
        public ViewsSelectSpec? Select { get; set; }

        public ViewsRenameSpec? Rename { get; set; }
    }

    public sealed class ViewsSelectSpec
    {
        public List<long>? ViewIds { get; set; }

        /// <summary>Alias of <see cref="ViewIds"/>: the same selector name as query_elements.</summary>
        public List<long>? ElementIds { get; set; }

        public string? NameContains { get; set; }

        public string? NameStartsWith { get; set; }

        public string? NameEndsWith { get; set; }

        public List<string>? ViewTypes { get; set; }

        public List<string>? Types { get; set; }

        public string? ViewType { get; set; }

        public string? Type { get; set; }

        public List<long>? LevelIds { get; set; }

        public List<string>? Folders { get; set; }

        public string? Folder { get; set; }

        public string? TemplateName { get; set; }

        /// <summary>Alias of <see cref="TemplateName"/>.</summary>
        public string? Template { get; set; }

        public long? TemplateId { get; set; }

        public bool? HasTemplate { get; set; }

        public bool? IsTemplate { get; set; }

        public bool? IncludeTemplates { get; set; }

        public bool? ExcludeTemplates { get; set; }

        public bool? ExcludeSheets { get; set; }

        public string? SheetNumber { get; set; }

        public bool? OnSheet { get; set; }

        public int? Limit { get; set; }
    }

    public sealed class ViewsRenameSpec
    {
        public string? Find { get; set; }

        public string? Replace { get; set; }

        public string? Prefix { get; set; }

        public string? Suffix { get; set; }

        public string? SetName { get; set; }

        public NumberingSpec? Numbering { get; set; }
    }

    public sealed class NumberingSpec
    {
        public int Start { get; set; } = 1;

        public int Step { get; set; } = 1;

        public int Padding { get; set; }

        public string? Format { get; set; }
    }

    public sealed class RenameUnitArgs
    {
        public long ViewId { get; set; }

        public string? OldName { get; set; }

        public string NewName { get; set; } = string.Empty;

        public string? TempName { get; set; }

        public string? Error { get; set; }
    }
}