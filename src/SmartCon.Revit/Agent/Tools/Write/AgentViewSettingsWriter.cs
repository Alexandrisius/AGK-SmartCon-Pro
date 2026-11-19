using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Applies one view-setting section (template / view range / crop / visibility)
/// to a single view and reports a before → after pair for the change set.
/// </summary>
internal static class AgentViewSettingsWriter
{
    internal sealed class Change
    {
        public bool Changed { get; set; }

        public string? ErrorCode { get; set; }

        public string? Reason { get; set; }

        public string? OldValue { get; set; }

        public string? NewValue { get; set; }

        public string? Detail { get; set; }

        public ChangeItem? ToChangeItem(View view, string key)
        {
            if (!Changed)
                return null;

            return new ChangeItem
            {
                UniqueId = view.UniqueId,
                ElementId = view.Id.GetValue(),
                ParameterName = key,
                OldValue = OldValue,
                NewValue = NewValue,
                Status = ChangeStatus.Applied
            };
        }

        internal static Change Skip(string reason) => new() { Reason = reason };

        internal static Change Error(string errorCode, string reason) =>
            new() { ErrorCode = errorCode, Reason = reason };
    }

    internal static Change ApplyTemplate(
        Document doc,
        View view,
        long templateId,
        bool overwrite)
    {
        if (!AgentViewWriteHelper.TryResolveTemplate(
                doc, view, templateId, out var template, out var code, out var reason))
        {
            return Change.Error(code, reason);
        }

        // Revit itself refuses cross-family templates (a Section template cannot be
        // assigned to a FloorPlan). The dry run does not assign, so validate here:
        // otherwise the preview promises a change that apply cannot deliver.
        if (template.ViewType != view.ViewType)
        {
            return Change.Error(
                "template_view_type_mismatch",
                "Template '" + template.Name + "' is " + template.ViewType +
                " and cannot be applied to " + view.ViewType + " view '" + view.Name + "'.");
        }

        var currentId = view.ViewTemplateId;
        if (currentId != ElementId.InvalidElementId
            && currentId.GetValue() == templateId)
        {
            return Change.Skip("The view already uses this template (no-op).");
        }

        if (currentId != ElementId.InvalidElementId && !overwrite)
        {
            var current = doc.GetElement(currentId);
            return Change.Skip(
                "View '" + view.Name + "' already uses template '" + current?.Name +
                "'; pass overwriteTemplate=true to replace it.");
        }

        var oldTemplate = currentId == ElementId.InvalidElementId
            ? null
            : doc.GetElement(currentId)?.Name;

        try
        {
            view.ViewTemplateId = ElementIdCompat.Create(templateId);
        }
        catch (Exception ex)
        {
            return Change.Error("template_not_applicable", ex.Message);
        }

        var applied = view.ViewTemplateId.GetValue();
        var change = new Change
        {
            Changed = true,
            OldValue = currentId == ElementId.InvalidElementId ? string.Empty : currentId.GetValue().ToString(),
            NewValue = applied.ToString(),
            Detail = "template '" + template.Name + "'" +
                     (oldTemplate is null ? string.Empty : " (was '" + oldTemplate + "')")
        };

        if (applied != templateId)
        {
            return Change.Error(
                "template_not_applicable",
                "Revit did not apply the template '" + template.Name + "' to '" + view.Name + "'.");
        }

        return change;
    }

    internal static Change ApplyViewRange(View view, ViewRangeSpec? spec)
    {
        if (spec is null || spec.IsEmpty)
            return Change.Skip("No view range specified.");

        if (view is not ViewPlan plan)
        {
            return Change.Error(
                "unsupported_view_type",
                "View range applies to plan views only; '" + view.Name + "' is " +
                view.ViewType + ".");
        }

        if (AgentViewSettingsReader.IsParameterLockedByTemplate(
                view, BuiltInParameter.PLAN_VIEW_RANGE, out var rangeTemplateId, out var rangeTemplate))
        {
            return Change.Error(
                "view_controlled_by_template",
                "The view range of '" + view.Name + "' is controlled by template '" +
                rangeTemplate + "'; writing it on the view has no effect. Detach it with " +
                "set_view_template_inclusion(templateId=" + rangeTemplateId +
                ", parameters=[\"viewRange\"], included=false), then retry.");
        }

        ViewRangeSpec? before;
        try
        {
            before = AgentViewWriteHelper.ReadViewRange(view);
        }
        catch (Exception ex)
        {
            return Change.Error("view_range_unavailable", ex.Message);
        }

        try
        {
            AgentViewWriteHelper.ApplyViewRange(plan, spec);
        }
        catch (Exception ex)
        {
            return Change.Error("invalid_view_range", ex.Message);
        }

        var after = AgentViewWriteHelper.ReadViewRange(view);
        var oldJson = AgentViewWriteHelper.Serialize(before ?? new ViewRangeSpec());
        var newJson = AgentViewWriteHelper.Serialize(after ?? new ViewRangeSpec());

        if (string.Equals(oldJson, newJson, StringComparison.Ordinal))
            return Change.Skip("The view range is already set to these values (no-op).");

        return new Change
        {
            Changed = true,
            OldValue = oldJson,
            NewValue = newJson,
            Detail = "view range"
        };
    }

    internal static Change ApplyCrop(
        Document doc,
        View view,
        ViewCropSpec? spec,
        List<string> warnings)
    {
        if (spec is null || spec.IsEmpty)
            return Change.Skip("No crop settings specified.");

        if (AgentViewSettingsReader.IsParameterLockedByTemplate(
                view, BuiltInParameter.VIEWER_CROP_REGION, out _, out var cropTemplate))
        {
            return Change.Error(
                "view_controlled_by_template",
                "The crop region of '" + view.Name + "' is controlled by template '" +
                cropTemplate + "'; writing it on the view has no effect. Revit does not allow " +
                "detaching crop from a template (and a template's crop cannot be edited through " +
                "the API): remove the template from this view, then set the crop.");
        }

        ViewCropState before;
        try
        {
            before = AgentViewWriteHelper.ReadCropState(view);
        }
        catch (Exception ex)
        {
            return Change.Error("crop_unavailable", ex.Message);
        }

        try
        {
            if (spec.CopyFromViewId.HasValue)
            {
                if (!AgentViewWriteHelper.TryGetView(
                        doc, spec.CopyFromViewId.Value, out var source, out var code, out var reason))
                {
                    return Change.Error(code, reason);
                }

                if (source.Id.GetValue() == view.Id.GetValue())
                    return Change.Skip("Source and target view are the same (no-op).");

                AgentViewWriteHelper.CopyCrop(source, view, warnings);
            }
            else
            {
                // Only explicit extents mean "make the crop rectangular": a call that
                // merely toggles active/visible must keep an existing crop sketch.
                var explicitExtents = spec.MinMm is not null || spec.MaxMm is not null;
                if (explicitExtents && before.ShapeSet)
                {
                    warnings.Add("The crop sketch of '" + view.Name +
                                 "' was replaced by the requested rectangular extents.");
                }

                var state = new ViewCropState
                {
                    Active = spec.Active ?? before.Active,
                    Visible = spec.Visible ?? before.Visible,
                    ShapeSet = explicitExtents ? false : before.ShapeSet,
                    ShapeMm = explicitExtents ? null : before.ShapeMm,
                    MinMm = spec.MinMm ?? before.MinMm,
                    MaxMm = spec.MaxMm ?? before.MaxMm
                };

                AgentViewWriteHelper.ApplyCropState(view, state, warnings);
            }
        }
        catch (Exception ex)
        {
            return Change.Error("crop_failed", ex.Message);
        }

        var after = AgentViewWriteHelper.ReadCropState(view);
        var oldJson = AgentViewWriteHelper.Serialize(before);
        var newJson = AgentViewWriteHelper.Serialize(after);

        if (string.Equals(oldJson, newJson, StringComparison.Ordinal))
            return Change.Skip("The crop region is already set to these values (no-op).");

        return new Change
        {
            Changed = true,
            OldValue = oldJson,
            NewValue = newJson,
            Detail = "crop region"
        };
    }

    internal static Change ApplyVisibility(
        Document doc,
        View view,
        ViewVisibilitySpec? spec,
        List<string> warnings)
    {
        if (spec is null || spec.IsEmpty)
            return Change.Skip("No visibility settings specified.");

        if (AgentViewSettingsReader.IsParameterLockedByTemplate(
                view, BuiltInParameter.VIS_GRAPHICS_MODEL, out var visibilityTemplateId, out var visibilityTemplate))
        {
            return Change.Error(
                "view_controlled_by_template",
                "Category visibility of '" + view.Name + "' is controlled by template '" +
                visibilityTemplate + "'; writing it on the view has no effect. Detach it with " +
                "set_view_template_inclusion(templateId=" + visibilityTemplateId +
                ", parameters=[\"visibility\"], included=false), then retry.");
        }

        var before = AgentViewWriteHelper.ReadVisibility(doc, view);

        try
        {
            AgentViewWriteHelper.ApplyVisibility(doc, view, spec, warnings);
        }
        catch (Exception ex)
        {
            return Change.Error("visibility_failed", ex.Message);
        }

        var after = AgentViewWriteHelper.ReadVisibility(doc, view);
        var oldJson = AgentViewWriteHelper.Serialize(before);
        var newJson = AgentViewWriteHelper.Serialize(after);

        if (string.Equals(oldJson, newJson, StringComparison.Ordinal))
            return Change.Skip("Visibility is already set to these values (no-op).");

        return new Change
        {
            Changed = true,
            OldValue = oldJson,
            NewValue = newJson,
            Detail = "visibility (" + before.HiddenCategoryIds.Count + " → " +
                     after.HiddenCategoryIds.Count + " hidden categories)"
        };
    }

    internal static Change MirrorVisibility(Document doc, View source, View target, List<string> warnings)
    {
        if (AgentViewSettingsReader.IsParameterLockedByTemplate(
                target, BuiltInParameter.VIS_GRAPHICS_MODEL, out var targetTemplateId, out var targetTemplate))
        {
            return Change.Error(
                "view_controlled_by_template",
                "Category visibility of '" + target.Name + "' is controlled by template '" +
                targetTemplate + "'; copying visibility onto it has no effect. Detach it with " +
                "set_view_template_inclusion(templateId=" + targetTemplateId +
                ", parameters=[\"visibility\"], included=false), then retry.");
        }

        var before = AgentViewWriteHelper.ReadVisibility(doc, target);

        try
        {
            AgentViewWriteHelper.MirrorVisibility(doc, source, target, warnings);
        }
        catch (Exception ex)
        {
            return Change.Error("visibility_failed", ex.Message);
        }

        var after = AgentViewWriteHelper.ReadVisibility(doc, target);
        var oldJson = AgentViewWriteHelper.Serialize(before);
        var newJson = AgentViewWriteHelper.Serialize(after);

        if (string.Equals(oldJson, newJson, StringComparison.Ordinal))
            return Change.Skip("Visibility already matches the source view (no-op).");

        return new Change
        {
            Changed = true,
            OldValue = oldJson,
            NewValue = newJson,
            Detail = "visibility from '" + source.Name + "'"
        };
    }

    internal static Change Rename(View view, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return Change.Skip("No new name specified.");

        var nameError = AgentViewWriteHelper.ValidateName(newName);
        if (nameError is not null)
            return Change.Error("invalid_name", nameError);

        var target = newName!.Trim();
        if (string.Equals(view.Name, target, StringComparison.Ordinal))
            return Change.Skip("The view already has this name (no-op).");

        var oldName = view.Name;
        try
        {
            view.Name = target;
        }
        catch (Exception ex)
        {
            return Change.Error("duplicate_name", ex.Message);
        }

        if (!string.Equals(view.Name, target, StringComparison.Ordinal))
        {
            return Change.Error(
                "duplicate_name",
                "Revit did not accept the name '" + target + "' for view '" + oldName + "'.");
        }

        return new Change
        {
            Changed = true,
            OldValue = oldName,
            NewValue = view.Name,
            Detail = "name"
        };
    }
}
