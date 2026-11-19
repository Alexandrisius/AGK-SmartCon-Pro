namespace SmartCon.Core.Models.Agent;

public sealed class ViewCard
{
    public long ViewId { get; init; }

    public required string UniqueId { get; init; }

    public required string Name { get; init; }

    public required string ViewType { get; init; }

    public long? LevelId { get; init; }

    public string? LevelName { get; init; }

    public double? ElevationMm { get; init; }

    public long? TemplateId { get; init; }

    public string? TemplateName { get; init; }

    public bool IsTemplate { get; init; }

    public bool IsOnSheet { get; init; }

    public string? SheetNumber { get; init; }

    /// <summary>Project-browser folder path from the current view organization.</summary>
    public string? BrowserFolder { get; init; }

    /// <summary>Same path split into grouping levels (root → leaf).</summary>
    public IReadOnlyList<string> BrowserFolderPath { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The parameter behind each grouping level: built-in (level, view type) or
    /// project/shared parameter (e.g. "Группа видов" = "Сводный").
    /// </summary>
    public IReadOnlyList<ViewBrowserFolderSegment> BrowserFolderParameters { get; init; } =
        Array.Empty<ViewBrowserFolderSegment>();

    /// <summary>False when the current browser-organization filter hides the view.</summary>
    public bool InBrowser { get; init; } = true;
}

/// <summary>One grouping level of the project-browser path for a view.</summary>
public sealed class ViewBrowserFolderSegment
{
    /// <summary>Folder name shown in the project browser (the parameter value).</summary>
    public required string Segment { get; init; }

    public long? ParameterId { get; init; }

    public string? ParameterName { get; init; }

    /// <summary>True for a shared parameter (project/company shared parameter).</summary>
    public bool IsShared { get; init; }

    /// <summary>True for a Revit built-in grouping parameter (level, view type, …).</summary>
    public bool IsBuiltIn { get; init; }
}

public sealed class LevelCard
{
    public long LevelId { get; init; }

    public required string Name { get; init; }

    public double ElevationMm { get; init; }

    public bool IsBuildingStory { get; init; }
}

public sealed class CategoryCard
{
    public required string Name { get; init; }

    public string? DisplayName { get; init; }

    public string? CategoryType { get; init; }

    public int ElementCount { get; init; }
}

public sealed class ProjectOverview
{
    public required string DocumentId { get; init; }

    public required string Title { get; init; }

    public string? PathName { get; init; }

    public required string RevitVersion { get; init; }

    public bool IsWorkshared { get; init; }

    public bool IsModified { get; init; }

    public string? ProjectNumber { get; init; }

    public string? ProjectName { get; init; }

    public string? Author { get; init; }

    public required string Units { get; init; }

    public int LevelCount { get; init; }

    public int ViewCount { get; init; }

    public int SheetCount { get; init; }

    public IReadOnlyList<LevelCard> Levels { get; init; } = Array.Empty<LevelCard>();

    public IReadOnlyList<CategoryCard> Categories { get; init; } = Array.Empty<CategoryCard>();
}

public sealed class ViewRangeLevel
{
    public long? LevelId { get; init; }

    public string? LevelName { get; init; }

    public double OffsetMm { get; init; }
}

public sealed class ViewRangeInfo
{
    public ViewRangeLevel? Top { get; init; }

    public ViewRangeLevel? CutPlane { get; init; }

    public ViewRangeLevel? Bottom { get; init; }

    public ViewRangeLevel? ViewDepth { get; init; }
}

public sealed class CropBoxInfo
{
    public bool Active { get; init; }

    public bool Visible { get; init; }

    public bool ShapeValid { get; init; }

    public double MinXMm { get; init; }

    public double MinYMm { get; init; }

    public double MaxXMm { get; init; }

    public double MaxYMm { get; init; }
}

public sealed class CategoryVisibilityInfo
{
    public long CategoryId { get; init; }

    public required string Category { get; init; }

    public string? BuiltInName { get; init; }

    public bool Hidden { get; init; }
}

public sealed class ViewSettings
{
    public long ViewId { get; init; }

    public string UniqueId { get; init; } = string.Empty;

    public required string Name { get; init; }

    public required string ViewType { get; init; }

    public long? TemplateId { get; init; }

    public string? TemplateName { get; init; }

    public bool HasTemplate { get; init; }

    public int? Scale { get; init; }

    public string? DetailLevel { get; init; }

    public string? Discipline { get; init; }

    public ViewRangeInfo? ViewRange { get; init; }

    public CropBoxInfo? Crop { get; init; }

    public IReadOnlyList<CategoryVisibilityInfo> HiddenCategories { get; init; } =
        Array.Empty<CategoryVisibilityInfo>();

    /// <summary>Total hidden categories, present even when the list is omitted.</summary>
    public int HiddenCategoryCount { get; init; }

    public IReadOnlyList<string> HiddenWorksets { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ControlledParameters { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NonControlledParameters { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Compact view-settings overview returned when the caller neither asks for
/// specific fields nor sets compact=false (tester report C4: the full payload
/// with hundreds of hidden categories dominated the context for simple
/// questions).
/// </summary>
public sealed class ViewSettingsSummary
{
    public long ViewId { get; init; }

    public required string Name { get; init; }

    public required string ViewType { get; init; }

    public long? TemplateId { get; init; }

    public string? TemplateName { get; init; }

    public bool HasTemplate { get; init; }

    public int? Scale { get; init; }

    public string? DetailLevel { get; init; }

    public string? Discipline { get; init; }

    public bool HasViewRange { get; init; }

    public bool? CropActive { get; init; }

    public bool? CropVisible { get; init; }

    public bool? CropShapeSet { get; init; }

    public int HiddenCategoryCount { get; init; }

    public int HiddenWorksetCount { get; init; }

    public int ControlledParameterCount { get; init; }

    public int NonControlledParameterCount { get; init; }
}

public sealed class ViewTemplateCard
{
    public long TemplateId { get; init; }

    public required string Name { get; init; }

    public required string ViewType { get; init; }

    public bool IncludesViewRange { get; init; }

    public bool IncludesCrop { get; init; }

    public bool IncludesVisibility { get; init; }
}