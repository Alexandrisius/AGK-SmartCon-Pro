using System.Text.Json;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>One condition of the generic view-filter DSL.</summary>
internal sealed class FilterConditionSpec
{
    public string? Field { get; set; }

    public string? Parameter { get; set; }

    public string Op { get; set; } = "eq";

    /// <summary>
    /// Unit of a numeric value: mm|cm|m|ft|m2|m3|raw. When omitted, the parameter's
    /// project display unit is used (same rule as query_elements), so "500" means
    /// 500 mm in a metric project instead of 500 feet.
    /// </summary>
    public string? Unit { get; set; }

    public JsonElement Value { get; set; }

    public bool HasValue { get; set; }
}

internal sealed class CreateViewFilterRequest
{
    public List<long> ViewIds { get; set; } = new();

    public string? Name { get; set; }

    public List<string> Categories { get; set; } = new();

    public List<FilterConditionSpec> Conditions { get; set; } = new();

    public string? Logic { get; set; }

    public string? Visibility { get; set; }

    public bool Atomic { get; set; }
}

internal sealed class SetFilterOverridesRequest
{
    public List<long> ViewIds { get; set; } = new();

    public long? FilterId { get; set; }

    public string? FilterName { get; set; }

    public string? Color { get; set; }

    public string? ProjectionLineColor { get; set; }

    public string? CutLineColor { get; set; }

    public string? SurfaceForegroundColor { get; set; }

    public string? SurfaceBackgroundColor { get; set; }

    public string? Pattern { get; set; }

    public int? Transparency { get; set; }

    public bool? Halftone { get; set; }

    public bool Clear { get; set; }

    public bool Atomic { get; set; }
}

/// <summary>Serializable snapshot of one filter applied to a view (undo payload).</summary>
internal sealed class ViewFilterEntry
{
    public long FilterId { get; set; }

    public string? Name { get; set; }

    /// <summary>«Фильтр включён» (View.GetIsFilterEnabled), not «Видимость».</summary>
    public bool Enabled { get; set; } = true;

    public bool Visible { get; set; }

    public ViewOverrideState Overrides { get; set; } = new();
}

internal sealed class ViewFilterState
{
    public List<ViewFilterEntry> Filters { get; set; } = new();
}

/// <summary>Serializable subset of OverrideGraphicSettings.</summary>
internal sealed class ViewOverrideState
{
    public string? ProjectionLineColor { get; set; }

    public string? CutLineColor { get; set; }

    public string? SurfaceForegroundColor { get; set; }

    public long? SurfaceForegroundPatternId { get; set; }

    public string? SurfaceBackgroundColor { get; set; }

    public long? SurfaceBackgroundPatternId { get; set; }

    public int Transparency { get; set; }

    public bool Halftone { get; set; }

    /// <summary>True for an empty override set (no colours, no patterns, no halftone).</summary>
    public bool IsEmpty =>
        ProjectionLineColor is null
        && CutLineColor is null
        && SurfaceForegroundColor is null
        && SurfaceForegroundPatternId is null
        && SurfaceBackgroundColor is null
        && SurfaceBackgroundPatternId is null
        && Transparency == 0
        && !Halftone;
}