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

        var hiddenCategories = ReadHiddenCategories(doc, view, out var hiddenCategoryCount);
        var templateView = template as View;

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
            DisplayStyle = SafeDisplayStyle(view),
            Discipline = SafeDiscipline(view),
            ViewRange = ReadViewRange(doc, view),
            Crop = ReadCrop(view),
            HiddenCategories = hiddenCategories,
            HiddenCategoryCount = hiddenCategoryCount,
            HiddenCategoryListCapped = hiddenCategoryCount > hiddenCategories.Count,
            HiddenCategoryEditableCount = CountEditableHiddenCategories(view, hiddenCategories),
            HiddenWorksets = ReadHiddenWorksets(doc, view),

            // N-ds-1: for a templated view the controlled/non-controlled split
            // belongs to its template — the view itself has no template ids,
            // so the fields projection used to answer two empty arrays exactly
            // when hasTemplate=true.
            ControlledParameters = templateView is not null
                ? ReadTemplateParameters(doc, templateView, controlled: true)
                : ReadTemplateParameters(doc, view, controlled: true),
            NonControlledParameters = templateView is not null
                ? ReadTemplateParameters(doc, templateView, controlled: false)
                : ReadTemplateParameters(doc, view, controlled: false)
        };
    }

    /// <summary>
    /// N-ds-2: view types without a Discipline property (legends, schedules...)
    /// throw on View.Discipline — one such view in a fields=[discipline] batch
    /// used to kill the whole call. Null is the honest "not applicable".
    /// </summary>
    private static string? SafeDiscipline(View view)
    {
        try
        {
            return view.Discipline.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Display style is the second half of "why is the view schematic?":
    /// Wireframe hides the fittings even at Fine detail. View types without
    /// display properties throw on the property, so null is the honest answer.
    /// </summary>
    private static string? SafeDisplayStyle(View view)
    {
        try
        {
            return view.HasDisplayStyle() ? view.DisplayStyle.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
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
            DisplayStyle = settings.DisplayStyle,
            Discipline = settings.Discipline,
            HasViewRange = settings.ViewRange is not null,
            CropActive = settings.Crop?.Active,
            CropVisible = settings.Crop?.Visible,
            CropShapeSet = settings.Crop?.ShapeValid,
            HiddenCategoryCount = settings.HiddenCategoryCount,
            HiddenCategoryListCapped = settings.HiddenCategoryListCapped,
            HiddenCategoryEditableCount = settings.HiddenCategoryEditableCount,
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
    /// View-setting sections (viewRange / visibility / crop) whose Include
    /// checkbox exists on the template but is unchecked: assigning the template
    /// leaves the view's own values (Autodesk: properties without Include
    /// "will not be overridden when you apply the view template"). The apply
    /// report spells these out (#457): a detached viewRange made 4 of 6 views
    /// keep a default range while the tool reported a clean success.
    /// Visibility spans two V/G parameters: both off → "visibility", exactly one
    /// off → "visibility-model" / "visibility-annotation".
    /// </summary>
    internal static IReadOnlyList<string> ReadIncludeSkippedSections(View template)
    {
        var skipped = new List<string>();
        try
        {
            if (IsWhitelistedButNotIncluded(template, BuiltInParameter.PLAN_VIEW_RANGE))
                skipped.Add("viewRange");
            var modelOff = IsWhitelistedButNotIncluded(
                template, BuiltInParameter.VIS_GRAPHICS_MODEL);
            var annotationOff = IsWhitelistedButNotIncluded(
                template, BuiltInParameter.VIS_GRAPHICS_ANNOTATION);
            if (modelOff && annotationOff)
                skipped.Add("visibility");
            else if (modelOff)
                skipped.Add("visibility-model");
            else if (annotationOff)
                skipped.Add("visibility-annotation");
            if (IsWhitelistedButNotIncluded(template, BuiltInParameter.VIEWER_CROP_REGION))
                skipped.Add("crop");
        }
        catch (Exception)
        {
            // The skip report is informational; it must never fail the apply.
        }

        return skipped;
    }

    private static bool IsWhitelistedButNotIncluded(
        View template,
        BuiltInParameter parameter)
    {
        var target = new ElementId(parameter).GetValue();
        var whitelisted = false;
        foreach (var id in template.GetTemplateParameterIds())
        {
            if (id.GetValue() != target)
                continue;

            whitelisted = true;
            break;
        }

        if (!whitelisted)
            return false;

        foreach (var id in template.GetNonControlledTemplateParameterIds())
        {
            if (id.GetValue() == target)
                return true;
        }

        return false;
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
        View view,
        out int total)
    {
        total = 0;
        var hidden = new List<CategoryVisibilityInfo>();
        foreach (Category category in doc.Settings.Categories)
        {
            try
            {
                if (!view.GetCategoryHidden(category.Id))
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            // Keep counting past the cap: the count claims to be the total, and a
            // 300-category "hide everything" view reported exactly 300.
            total++;
            if (hidden.Count >= MaxHiddenCategories)
                continue;

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

    /// <summary>
    /// How many of the already hidden categories Revit still allows to toggle on
    /// this view (<c>CanCategoryBeHidden</c>). The rest are hidden but not editable
    /// here: the V/G parameter may be controlled by the view template, or the
    /// category cannot be hidden in this view type. The counter does not claim
    /// which reason applies.
    /// </summary>
    private static int CountEditableHiddenCategories(
        View view,
        IReadOnlyList<CategoryVisibilityInfo> hidden)
    {
        var editable = 0;
        foreach (var entry in hidden)
        {
            if (CanToggleCategory(view, entry.CategoryId))
                editable++;
        }

        return editable;
    }

    /// <summary>
    /// <c>CanCategoryBeHidden</c> is a capability of the view type, not a
    /// statement about the template: false means "cannot be toggled through the
    /// API on this view". Shared by the reader and the writer so read and write
    /// counters always agree.
    /// </summary>
    internal static bool CanToggleCategory(View view, long categoryId)
    {
        try
        {
            return view.CanCategoryBeHidden(ElementIdCompat.Create(categoryId));
        }
        catch (Exception)
        {
            return false;
        }
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

    private static IReadOnlyList<string> ReadTemplateParameters(
        Document doc,
        View view,
        bool controlled)
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
                    names.Add(DescribeTemplateParameter(doc, id));
            }

            return names;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// A template parameter is often a project/shared parameter element, not a
    /// built-in: its id is positive and AgentBuiltInParameterNames falls back to
    /// "#13122". Resolve the element name so the model does not have to map ids to
    /// names by hand (external benchmark, tasks 1 and 3).
    /// </summary>
    private static string DescribeTemplateParameter(Document doc, ElementId id)
    {
        var name = AgentBuiltInParameterNames.GetName(id);
        if (!name.StartsWith("#", StringComparison.Ordinal))
            return name;

        try
        {
            var element = doc.GetElement(id);
            if (element is not null && !string.IsNullOrWhiteSpace(element.Name))
                return element.Name + " (#" + id.GetValue() + ")";
        }
        catch (Exception)
        {
            // Fall back to the raw id below.
        }

        return name;
    }
}