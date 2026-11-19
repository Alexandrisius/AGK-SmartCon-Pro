using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Query;

internal static class AgentViewSettingsReader
{
    private const int MaxHiddenCategories = 300;

    internal static ViewSettings Read(Document doc, View view)
    {
        var templateId = view.ViewTemplateId;
        var hasTemplate = templateId != ElementId.InvalidElementId;
        var template = hasTemplate ? doc.GetElement(templateId) : null;

        return new ViewSettings
        {
            ViewId = view.Id.GetValue(),
            UniqueId = view.UniqueId,
            Name = view.Name,
            ViewType = view.ViewType.ToString(),
            TemplateId = hasTemplate ? templateId.GetValue() : null,
            TemplateName = template?.Name,
            HasTemplate = hasTemplate,
            Scale = view.Scale,
            DetailLevel = view.DetailLevel.ToString(),
            Discipline = view.Discipline.ToString(),
            ViewRange = ReadViewRange(doc, view),
            Crop = ReadCrop(view),
            HiddenCategories = ReadHiddenCategories(doc, view),
            HiddenWorksets = ReadHiddenWorksets(doc, view),
            ControlledParameters = ReadTemplateParameters(view, controlled: true),
            NonControlledParameters = ReadTemplateParameters(view, controlled: false)
        };
    }

    internal static ViewTemplateCard CreateTemplateCard(Document doc, View template)
    {
        return new ViewTemplateCard
        {
            TemplateId = template.Id.GetValue(),
            Name = template.Name,
            ViewType = template.ViewType.ToString(),
            IncludesViewRange = IsTemplateParameterControlled(
                template,
                BuiltInParameter.PLAN_VIEW_RANGE),
            IncludesCrop = IsTemplateParameterControlled(
                template,
                BuiltInParameter.VIEWER_CROP_REGION),
            IncludesVisibility = IsTemplateParameterControlled(
                template,
                BuiltInParameter.VIS_GRAPHICS_MODEL)
        };
    }

    internal static ViewSettingsSummary CreateSummary(ViewSettings settings)
    {
        return new ViewSettingsSummary
        {
            ViewId = settings.ViewId,
            Name = settings.Name,
            ViewType = settings.ViewType,
            TemplateId = settings.TemplateId,
            TemplateName = settings.TemplateName,
            HasTemplate = settings.HasTemplate,
            Scale = settings.Scale,
            DetailLevel = settings.DetailLevel,
            Discipline = settings.Discipline,
            HasViewRange = settings.ViewRange is not null,
            CropActive = settings.Crop?.Active,
            CropVisible = settings.Crop?.Visible,
            CropShapeSet = settings.Crop?.ShapeValid,
            HiddenCategoryCount = settings.HiddenCategories.Count,
            HiddenWorksetCount = settings.HiddenWorksets.Count,
            ControlledParameterCount = settings.ControlledParameters.Count,
            NonControlledParameterCount = settings.NonControlledParameters.Count
        };
    }

    internal static bool IsTemplateParameterControlled(
        View template,
        BuiltInParameter parameter)
    {
        try
        {
            var target = new ElementId(parameter).GetValue();
            foreach (var id in template.GetNonControlledTemplateParameterIds())
            {
                if (id.GetValue() == target)
                    return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the view gets <paramref name="parameter"/> from its template, so
    /// a local write would be silently ignored. Writes must fail with
    /// <c>view_controlled_by_template</c> instead of reporting a no-op or a
    /// success that read-back cannot confirm.
    /// </summary>
    internal static bool IsParameterLockedByTemplate(
        View view,
        BuiltInParameter parameter,
        out long? templateId,
        out string? templateName)
    {
        templateId = null;
        templateName = null;

        var id = view.ViewTemplateId;
        if (id is null || id == ElementId.InvalidElementId)
            return false;

        templateId = id.GetValue();
        if (view.Document.GetElement(id) is not View template)
            return false;

        templateName = template.Name;
        return IsTemplateParameterControlled(template, parameter);
    }

    private static ViewRangeInfo? ReadViewRange(Document doc, View view)
    {
        if (view is not ViewPlan plan)
            return null;

        PlanViewRange range;
        try
        {
            range = plan.GetViewRange();
        }
        catch (Exception)
        {
            return null;
        }

        return new ViewRangeInfo
        {
            Top = ReadPlane(doc, range, PlanViewPlane.TopClipPlane),
            CutPlane = ReadPlane(doc, range, PlanViewPlane.CutPlane),
            Bottom = ReadPlane(doc, range, PlanViewPlane.BottomClipPlane),
            ViewDepth = ReadPlane(doc, range, PlanViewPlane.ViewDepthPlane)
        };
    }

    private static ViewRangeLevel ReadPlane(
        Document doc,
        PlanViewRange range,
        PlanViewPlane plane)
    {
        var levelId = range.GetLevelId(plane);
        var level = levelId == ElementId.InvalidElementId
            ? null
            : doc.GetElement(levelId) as Level;

        return new ViewRangeLevel
        {
            LevelId = level is null ? null : level.Id.GetValue(),
            LevelName = level?.Name,
            OffsetMm = AgentUnitsFormat.ToMillimeters(range.GetOffset(plane))
        };
    }

    private static CropBoxInfo ReadCrop(View view)
    {
        try
        {
            return ReadCropCore(view);
        }
        catch (Exception)
        {
            // View templates and some non-graphical views reject CropBoxActive.
            return new CropBoxInfo();
        }
    }

    private static CropBoxInfo ReadCropCore(View view)
    {
        var shapeSet = false;
        try
        {
            shapeSet = view.GetCropRegionShapeManager()?.ShapeSet ?? false;
        }
        catch (Exception)
        {
        }

        var active = view.CropBoxActive;
        var visible = view.CropBoxVisible;
        var box = view.CropBox;
        if (box is null)
        {
            return new CropBoxInfo
            {
                Active = active,
                Visible = visible,
                ShapeValid = shapeSet
            };
        }

        return new CropBoxInfo
        {
            Active = active,
            Visible = visible,
            ShapeValid = shapeSet,
            MinXMm = AgentUnitsFormat.ToMillimeters(box.Min.X),
            MinYMm = AgentUnitsFormat.ToMillimeters(box.Min.Y),
            MaxXMm = AgentUnitsFormat.ToMillimeters(box.Max.X),
            MaxYMm = AgentUnitsFormat.ToMillimeters(box.Max.Y)
        };
    }

    private static IReadOnlyList<CategoryVisibilityInfo> ReadHiddenCategories(
        Document doc,
        View view)
    {
        var hidden = new List<CategoryVisibilityInfo>();
        foreach (Category category in doc.Settings.Categories)
        {
            if (hidden.Count >= MaxHiddenCategories)
                break;

            try
            {
                if (!view.GetCategoryHidden(category.Id))
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            hidden.Add(new CategoryVisibilityInfo
            {
                CategoryId = category.Id.GetValue(),
                Category = category.Name,
                BuiltInName = AgentElementCardFactory.GetCategoryName(category),
                Hidden = true
            });
        }

        return hidden;
    }

    private static IReadOnlyList<string> ReadHiddenWorksets(Document doc, View view)
    {
        if (!doc.IsWorkshared)
            return Array.Empty<string>();

        var hidden = new List<string>();
        try
        {
            foreach (var workset in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                if (view.GetWorksetVisibility(workset.Id) == WorksetVisibility.Hidden)
                    hidden.Add(workset.Name);
            }
        }
        catch (Exception)
        {
        }

        return hidden;
    }

    private static IReadOnlyList<string> ReadTemplateParameters(View view, bool controlled)
    {
        if (!view.IsTemplate)
            return Array.Empty<string>();

        try
        {
            var excluded = new HashSet<long>();
            foreach (var id in view.GetNonControlledTemplateParameterIds())
                excluded.Add(id.GetValue());

            var names = new List<string>();
            foreach (var id in view.GetTemplateParameterIds())
            {
                var isControlled = !excluded.Contains(id.GetValue());
                if (isControlled == controlled)
                    names.Add(AgentBuiltInParameterNames.GetName(id));
            }

            return names;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }
}