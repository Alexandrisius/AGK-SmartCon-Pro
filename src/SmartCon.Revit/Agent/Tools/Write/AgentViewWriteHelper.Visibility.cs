using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Visibility read/apply half of the view write helper: category allow-lists,
/// notHidable reporting and the hidden-category state used by undo.
/// </summary>
internal static partial class AgentViewWriteHelper
{
    /// <summary>
    /// Every hidden category of the view, including the ones the template hides.
    /// The read must match <c>get_view_settings</c>: a write that reported
    /// "16 → 17" while the view had 24 hidden categories made the numbers look
    /// wrong and cost the model a diagnostic round (external benchmark, task 5).
    /// Write guards stay where they belong (CanCategoryBeHidden in apply/undo).
    /// </summary>
    internal static ViewVisibilityState ReadVisibility(Document doc, View view)
    {
        var state = new ViewVisibilityState();
        foreach (Category category in doc.Settings.Categories)
        {
            try
            {
                if (view.GetCategoryHidden(category.Id))
                    state.HiddenCategoryIds.Add(category.Id.GetValue());
            }
            catch (Exception)
            {
                // Some categories reject both calls (import/analytical); skip them.
            }
        }

        return state;
    }

    internal static void MirrorVisibility(
        Document doc,
        View source,
        View target,
        List<string> warnings)
    {
        var desired = new HashSet<long>(ReadVisibility(doc, source).HiddenCategoryIds);
        var applied = 0;
        string? lastError = null;

        foreach (Category category in doc.Settings.Categories)
        {
            try
            {
                if (!target.CanCategoryBeHidden(category.Id))
                    continue;

                var shouldHide = desired.Contains(category.Id.GetValue());
                if (target.GetCategoryHidden(category.Id) == shouldHide)
                    continue;

                target.SetCategoryHidden(category.Id, shouldHide);
                applied++;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        if (lastError is not null)
        {
            warnings.Add("Some categories could not be mirrored to '" + target.Name +
                         "' (" + applied + " applied): " + lastError);
        }
    }

    internal static void ApplyVisibilityState(Document doc, View view, ViewVisibilityState state)
    {
        var desired = new HashSet<long>(state.HiddenCategoryIds);
        var known = new HashSet<long>();
        foreach (Category category in doc.Settings.Categories)
        {
            known.Add(category.Id.GetValue());
            try
            {
                if (!view.CanCategoryBeHidden(category.Id))
                    continue;

                var shouldHide = desired.Contains(category.Id.GetValue());
                if (view.GetCategoryHidden(category.Id) != shouldHide)
                    view.SetCategoryHidden(category.Id, shouldHide);
            }
            catch (Exception)
            {
                // Categories that reject both calls cannot be restored; the
                // caller reports the overall result.
            }
        }

        // Categories outside Settings.Categories (cameras) were part of the
        // recorded state too; undo must restore their hidden flag as well.
        foreach (var id in desired)
        {
            if (known.Contains(id))
                continue;

            try
            {
                var categoryId = ElementIdCompat.Create(id);
                if (view.CanCategoryBeHidden(categoryId) && !view.GetCategoryHidden(categoryId))
                    view.SetCategoryHidden(categoryId, true);
            }
            catch (Exception)
            {
                // Not restorable through the API; nothing else to do.
            }
        }
    }

    /// <summary>
    /// True when a requested category's V/G group is controlled by the view
    /// template. Revit models categories in three separate V/G parameters; the
    /// old check looked only at VIS_GRAPHICS_MODEL, so annotation categories
    /// silently ignored SetCategoryHidden when the template controlled them.
    /// </summary>
    internal static bool TryGetVisibilityTemplateLock(
        Document doc,
        View view,
        ViewVisibilitySpec spec,
        out long? templateId,
        out string? templateName,
        out string parameterLabel)
    {
        templateId = null;
        templateName = null;
        parameterLabel = string.Empty;
        if ((spec.Categories is null || spec.Categories.Count == 0)
            && (spec.HideAllExcept is null || spec.HideAllExcept.Count == 0))
            return false;

        var entries = (spec.Categories ?? new List<CategoryVisibilitySpec>())
            .Concat(spec.HideAllExcept ?? new List<CategoryVisibilitySpec>());
        foreach (var entry in entries)
        {
            if (!TryResolveCategory(doc, entry, out var categoryId))
                continue;

            Category? category;
            try
            {
                category = Category.GetCategory(doc, categoryId);
            }
            catch (Exception)
            {
                continue;
            }

            if (category is null)
                continue;

            BuiltInParameter parameter;
            string label;
            switch (category.CategoryType)
            {
                case CategoryType.Annotation:
                    parameter = BuiltInParameter.VIS_GRAPHICS_ANNOTATION;
                    label = "annotation (VIS_GRAPHICS_ANNOTATION)";
                    break;
                case CategoryType.AnalyticalModel:
                    parameter = BuiltInParameter.VIS_GRAPHICS_ANALYTICAL_MODEL;
                    label = "analytical model (VIS_GRAPHICS_ANALYTICAL_MODEL)";
                    break;
                default:
                    parameter = BuiltInParameter.VIS_GRAPHICS_MODEL;
                    label = "model (VIS_GRAPHICS_MODEL)";
                    break;
            }

            if (AgentViewSettingsReader.IsParameterLockedByTemplate(
                    view, parameter, out var lockedTemplateId, out var lockedTemplateName))
            {
                templateId = lockedTemplateId;
                templateName = lockedTemplateName;
                parameterLabel = label;
                return true;
            }
        }

        return false;
    }

    internal static void ApplyVisibility(
        Document doc,
        View view,
        ViewVisibilitySpec spec,
        List<string> warnings)
    {
        ApplyVisibility(doc, view, spec, warnings, out _);
    }

    /// <summary>
    /// Applies an allow-list (<c>hideAllExcept</c>) as well as explicit category
    /// entries. Returns the categories the API could not touch: the caller must
    /// publish them in <c>notHidable[]</c>, because "success with changed=1"
    /// next to a visible stair is exactly how the owner's view stayed dirty.
    /// </summary>
    internal static void ApplyVisibility(
        Document doc,
        View view,
        ViewVisibilitySpec spec,
        List<string> warnings,
        out VisibilityApplyResult result)
    {
        result = new VisibilityApplyResult();

        if (spec.Categories is not null)
        {
            foreach (var entry in spec.Categories)
            {
                var lookup = ResolveCategory(doc, entry, out var categoryId);
                if (lookup == CategoryLookup.Unknown)
                {
                    warnings.Add("Category '" + (entry.Category ?? entry.CategoryId?.ToString()) +
                                 "' was not found in the document and was skipped.");
                    continue;
                }

                var label = LabelOf(doc, entry, categoryId);
                if (TrySetVisibility(view, categoryId, entry.Hidden, label, lookup, result)
                    && lookup == CategoryLookup.NotInDocument)
                {
                    // A built-in category outside doc.Settings.Categories still
                    // belongs to the read-back/undo state.
                    result.ExtraCategoryIds.Add(categoryId.GetValue());
                }
            }
        }

        if (spec.HideAllExcept is { Count: > 0 })
        {
            var keep = new HashSet<long>();
            foreach (var entry in spec.HideAllExcept)
            {
                var lookup = ResolveCategory(doc, entry, out var categoryId);
                if (lookup == CategoryLookup.Unknown)
                {
                    warnings.Add("Category '" + (entry.Category ?? entry.CategoryId?.ToString()) +
                                 "' was not found in the document and cannot be kept visible.");
                    continue;
                }

                keep.Add(categoryId.GetValue());
                result.ExtraCategoryIds.Add(categoryId.GetValue());
                if (lookup == CategoryLookup.NotInDocument)
                {
                    // Not in Settings.Categories, so the loop below never sees it:
                    // try to show it here and report it if the API refuses.
                    TrySetVisibility(
                        view, categoryId, hidden: false, LabelOf(doc, entry, categoryId), lookup, result);
                }
            }

            foreach (Category category in doc.Settings.Categories)
            {
                var categoryId = category.Id;
                var id = categoryId.GetValue();
                try
                {
                    if (keep.Contains(id))
                    {
                        if (view.CanCategoryBeHidden(categoryId) && view.GetCategoryHidden(categoryId))
                        {
                            view.SetCategoryHidden(categoryId, false);
                            result.ShownByInverse++;
                        }

                        continue;
                    }

                    // Every other category goes hidden. Categories the API cannot
                    // toggle are left alone silently: hideAllExcept is a bulk mode
                    // and reporting all view-only categories would drown the answer.
                    if (!view.CanCategoryBeHidden(categoryId))
                        continue;

                    if (!view.GetCategoryHidden(categoryId))
                    {
                        view.SetCategoryHidden(categoryId, true);
                        result.HiddenByInverse++;
                    }
                }
                catch (Exception)
                {
                    // Reverse-mode failures are not actionable one by one; the
                    // system-view contract check (summarize_elements) is the gate.
                }
            }
        }

        if (spec.Worksets is { Count: > 0 })
        {
            warnings.Add("Workset visibility is not supported: the active document is not " +
                         "workshared, so there are no user worksets.");
        }
    }

    private static bool TrySetVisibility(
        View view,
        ElementId categoryId,
        bool hidden,
        string label,
        CategoryLookup lookup,
        VisibilityApplyResult result)
    {
        try
        {
            if (!view.CanCategoryBeHidden(categoryId))
            {
                result.NotHidable.Add(new NotHidableCategory
                {
                    Category = label,
                    CategoryId = categoryId.GetValue(),
                    Reason = DescribeNotHidable(view, lookup)
                });
                return false;
            }

            view.SetCategoryHidden(categoryId, hidden);
            return true;
        }
        catch (Exception ex)
        {
            result.NotHidable.Add(new NotHidableCategory
            {
                Category = label,
                CategoryId = categoryId.GetValue(),
                Reason = "Revit refused SetCategoryHidden: " + ex.Message +
                         " Hide it in the Revit UI (Visibility/Graphics) or hide its parent category."
            });
            return false;
        }
    }

    private static string DescribeNotHidable(View view, CategoryLookup lookup)
    {
        if (lookup == CategoryLookup.NotInDocument)
        {
            return "Revit does not expose this category for V/G control (cameras and " +
                   "some view-only categories); the element stays visible. Hide it in the " +
                   "Revit UI (Visibility/Graphics).";
        }

        return "Revit refuses to toggle this category in a " + view.ViewType +
               " view: it is controlled through its parent category or a view-type " +
               "limitation. Hide the parent category, or use Visibility/Graphics in the " +
               "Revit UI.";
    }

    private static string LabelOf(
        Document doc,
        CategoryVisibilitySpec entry,
        ElementId categoryId)
    {
        if (!string.IsNullOrWhiteSpace(entry.Category))
            return entry.Category!;
        return CategoryName(doc, categoryId.GetValue()) ?? ("#" + categoryId.GetValue());
    }

    internal static List<ElementId> ResolvedCategoryIds(
        Document doc,
        ViewVisibilitySpec spec)
    {
        var ids = new List<ElementId>();
        foreach (var entry in (spec.Categories ?? new List<CategoryVisibilitySpec>())
                     .Concat(spec.HideAllExcept ?? new List<CategoryVisibilitySpec>()))
        {
            if (ResolveCategory(doc, entry, out var categoryId) != CategoryLookup.Unknown
                && categoryId != ElementId.InvalidElementId)
            {
                ids.Add(categoryId);
            }
        }

        return ids;
    }

    internal enum CategoryLookup
    {
        Resolved,
        NotInDocument,
        Unknown
    }

    /// <summary>
    /// Resolves an entry to a category id. A valid BuiltInCategory that Revit
    /// does not list in <c>Settings.Categories</c> (cameras) is still a real
    /// category id — "not found in the document" was a wrong diagnosis and cost
    /// the owner a round. The caller decides whether it is hideable.
    /// </summary>
    internal static CategoryLookup ResolveCategory(
        Document doc,
        CategoryVisibilitySpec spec,
        out ElementId categoryId)
    {
        categoryId = ElementId.InvalidElementId;

        if (spec.CategoryId.HasValue)
        {
            var byId = Category.GetCategory(doc, ElementIdCompat.Create(spec.CategoryId.Value));
            if (byId is not null)
            {
                categoryId = byId.Id;
                return CategoryLookup.Resolved;
            }

            categoryId = ElementIdCompat.Create(spec.CategoryId.Value);
            return CategoryLookup.NotInDocument;
        }

        if (string.IsNullOrWhiteSpace(spec.Category))
            return CategoryLookup.Unknown;

        var name = spec.Category!;
        if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var builtIn))
        {
            var category = Category.GetCategory(doc, builtIn);
            if (category is not null)
            {
                categoryId = category.Id;
                return CategoryLookup.Resolved;
            }

            categoryId = ElementIdCompat.Create((long)(int)builtIn);
            return CategoryLookup.NotInDocument;
        }

        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                categoryId = category.Id;
                return CategoryLookup.Resolved;
            }
        }

        return CategoryLookup.Unknown;
    }

    internal static bool TryResolveCategory(
        Document doc,
        CategoryVisibilitySpec spec,
        out ElementId categoryId) =>
        ResolveCategory(doc, spec, out categoryId) != CategoryLookup.Unknown;

    internal sealed class VisibilityApplyResult
    {
        public List<NotHidableCategory> NotHidable { get; } = new();

        public List<long> ExtraCategoryIds { get; } = new();

        public int HiddenByInverse { get; set; }

        public int ShownByInverse { get; set; }
    }

}
