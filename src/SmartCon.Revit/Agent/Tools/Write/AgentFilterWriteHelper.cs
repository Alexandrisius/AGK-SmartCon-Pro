using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Shared read/apply helpers for the filter write tools: filter lookup, view
/// filter state snapshots (for undo), override conversion and colour parsing.
/// </summary>
internal static class AgentFilterWriteHelper
{
    private static readonly Dictionary<string, (int R, int G, int B)> NamedColors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = (220, 30, 30),
            ["красный"] = (220, 30, 30),
            ["green"] = (30, 160, 60),
            ["зеленый"] = (30, 160, 60),
            ["зелёный"] = (30, 160, 60),
            ["blue"] = (30, 80, 200),
            ["синий"] = (30, 80, 200),
            ["yellow"] = (240, 200, 30),
            ["желтый"] = (240, 200, 30),
            ["жёлтый"] = (240, 200, 30),
            ["orange"] = (240, 140, 30),
            ["оранжевый"] = (240, 140, 30),
            ["magenta"] = (200, 40, 160),
            ["пурпурный"] = (200, 40, 160),
            ["cyan"] = (30, 170, 200),
            ["голубой"] = (30, 170, 200),
            ["grey"] = (128, 128, 128),
            ["gray"] = (128, 128, 128),
            ["серый"] = (128, 128, 128),
            ["black"] = (20, 20, 20),
            ["черный"] = (20, 20, 20),
            ["чёрный"] = (20, 20, 20),
            ["white"] = (255, 255, 255),
            ["белый"] = (255, 255, 255)
        };

    internal static ParameterFilterElement? FindByName(Document doc, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(ParameterFilterElement)))
        {
            if (element is ParameterFilterElement filter
                && string.Equals(filter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return filter;
            }
        }

        return null;
    }

    internal static ParameterFilterElement? FindById(Document doc, long filterId) =>
        doc.GetElement(ElementIdCompat.Create(filterId)) as ParameterFilterElement;

    internal static IReadOnlyList<string> KnownFilterNames(Document doc, int max)
    {
        var names = new List<string>();
        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(ParameterFilterElement)))
        {
            if (element is ParameterFilterElement filter)
                names.Add(filter.Name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        if (names.Count > max)
            names.RemoveRange(max, names.Count - max);
        return names;
    }

    internal static string UniqueName(Document doc, string baseName)
    {
        var name = baseName.Trim();
        if (name.Length == 0)
            name = "SmartCon filter";

        if (FindByName(doc, name) is null)
            return name;

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = name + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
            if (FindByName(doc, candidate) is null)
                return candidate;
        }

        return name + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
    }

    internal static bool SupportsFilters(View view) => AgentFilterUsage.SupportsFilters(view);

    /// <summary>
    /// True when the view template controls V/G overrides, so a filter added to
    /// the view would be silently ignored.
    /// </summary>
    internal static bool IsFilterControlLockedByTemplate(View view, out long? templateId)
    {
        templateId = null;
        var id = view.ViewTemplateId;
        if (id is null || id == ElementId.InvalidElementId)
            return false;

        templateId = id.GetValue();
        var template = view.Document.GetElement(id) as View;
        if (template is null)
            return false;

        return AgentViewSettingsReader.IsTemplateParameterControlled(
            template, BuiltInParameter.VIS_GRAPHICS_FILTERS);
    }

    internal static ViewFilterState ReadState(View view)
    {
        var state = new ViewFilterState();
        foreach (var rawFilterId in AgentFilterUsage.FiltersOf(view))
        {
            var filterId = ElementIdCompat.Create(rawFilterId);
            var filter = view.Document.GetElement(filterId);
            var entry = new ViewFilterEntry
            {
                FilterId = filterId.GetValue(),
                Name = filter?.Name,
                Visible = SafeVisibility(view, filterId)
            };

            try
            {
                entry.Overrides = Describe(view.GetFilterOverrides(filterId));
            }
            catch (Exception)
            {
                entry.Overrides = new ViewOverrideState();
            }

            state.Filters.Add(entry);
        }

        state.Filters.Sort((left, right) => left.FilterId.CompareTo(right.FilterId));
        return state;
    }

    private static bool SafeVisibility(View view, ElementId filterId)
    {
        try
        {
            return view.GetFilterVisibility(filterId);
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal static string SerializeState(ViewFilterState state) =>
        JsonSerializer.Serialize(state, Options);

    internal static ViewFilterState? DeserializeState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ViewFilterState>(json!, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ViewOverrideState Describe(OverrideGraphicSettings settings) =>
        new()
        {
            ProjectionLineColor = FormatColor(settings.ProjectionLineColor),
            CutLineColor = FormatColor(settings.CutLineColor),
            SurfaceForegroundColor = FormatColor(settings.SurfaceForegroundPatternColor),
            SurfaceForegroundPatternId = ValidId(settings.SurfaceForegroundPatternId),
            SurfaceBackgroundColor = FormatColor(settings.SurfaceBackgroundPatternColor),
            SurfaceBackgroundPatternId = ValidId(settings.SurfaceBackgroundPatternId),
            Transparency = settings.Transparency,
            Halftone = settings.Halftone
        };

    internal static OverrideGraphicSettings ToSettings(ViewOverrideState state)
    {
        var settings = new OverrideGraphicSettings();
        var projection = ParseStoredColor(state.ProjectionLineColor);
        if (projection is not null)
            settings.SetProjectionLineColor(projection);

        var cut = ParseStoredColor(state.CutLineColor);
        if (cut is not null)
            settings.SetCutLineColor(cut);

        var surface = ParseStoredColor(state.SurfaceForegroundColor);
        if (surface is not null)
            settings.SetSurfaceForegroundPatternColor(surface);

        if (state.SurfaceForegroundPatternId is > 0)
        {
            settings.SetSurfaceForegroundPatternId(
                ElementIdCompat.Create(state.SurfaceForegroundPatternId.Value));
            settings.SetSurfaceForegroundPatternVisible(true);
        }

        var background = ParseStoredColor(state.SurfaceBackgroundColor);
        if (background is not null)
            settings.SetSurfaceBackgroundPatternColor(background);

        if (state.SurfaceBackgroundPatternId is > 0)
        {
            settings.SetSurfaceBackgroundPatternId(
                ElementIdCompat.Create(state.SurfaceBackgroundPatternId.Value));
            settings.SetSurfaceBackgroundPatternVisible(true);
        }

        settings.SetSurfaceTransparency(Math.Max(0, Math.Min(100, state.Transparency)));
        settings.SetHalftone(state.Halftone);
        return settings;
    }

    private static Color? ParseStoredColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return TryParseColor(value!, out var color, out _) ? color : null;
    }

    internal static string? FormatColor(Color? color)
    {
        if (color is null || !color.IsValid)
            return null;

        return "#" + color.Red.ToString("X2", CultureInfo.InvariantCulture)
                     + color.Green.ToString("X2", CultureInfo.InvariantCulture)
                     + color.Blue.ToString("X2", CultureInfo.InvariantCulture);
    }

    internal static bool TryParseColor(string text, out Color color, out string? error)
    {
        color = Color.InvalidColorValue;
        error = null;
        var value = text.Trim();
        if (value.Length == 0)
        {
            error = "Empty colour value.";
            return false;
        }

        if (NamedColors.TryGetValue(value, out var named))
        {
            color = new Color((byte)named.R, (byte)named.G, (byte)named.B);
            return true;
        }

        var hex = value.TrimStart('#');
        if (hex.Length == 6
            && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            color = new Color(
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
            return true;
        }

        error = "'" + text + "' is not a colour. Use #RRGGBB or a known name " +
                "(red, green, blue, yellow, orange, grey, black, white and Russian equivalents).";
        return false;
    }

    internal static FillPatternElement? FindPattern(Document doc, string? name)
    {
        var patterns = new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .ToList();

        if (string.IsNullOrWhiteSpace(name) || IsSolidName(name!))
        {
            foreach (var pattern in patterns)
            {
                if (pattern.GetFillPattern().IsSolidFill)
                    return pattern;
            }

            return null;
        }

        foreach (var pattern in patterns)
        {
            var fillPattern = pattern.GetFillPattern();
            if (fillPattern is not null
                && string.Equals(fillPattern.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return pattern;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> KnownPatternNames(Document doc, int max)
    {
        var names = new List<string>();
        foreach (var pattern in new FilteredElementCollector(doc)
                     .OfClass(typeof(FillPatternElement))
                     .Cast<FillPatternElement>())
        {
            var name = pattern.GetFillPattern()?.Name;
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name!);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        if (names.Count > max)
            names.RemoveRange(max, names.Count - max);
        return names;
    }

    internal static bool IsSolidName(string name) =>
        name.Trim().ToLowerInvariant() is "solid" or "сплошная" or "сплошная заливка"
            or "заливка" or "solid fill";

    private static long? ValidId(ElementId? id) =>
        id is null || id == ElementId.InvalidElementId ? null : id.GetValue();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}