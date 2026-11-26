using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

internal static class AgentElementEssence
{
    private const int MaxEntries = 12;

    private static readonly BuiltInParameter[] Common =
    {
        BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM,
        BuiltInParameter.ALL_MODEL_TYPE_MARK,
        BuiltInParameter.ALL_MODEL_MARK
    };

    private static readonly BuiltInParameter[] MepCurve =
    {
        BuiltInParameter.RBS_SYSTEM_NAME_PARAM,
        BuiltInParameter.RBS_SYSTEM_ABBREVIATION_PARAM,
        BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
        BuiltInParameter.RBS_CURVE_WIDTH_PARAM,
        BuiltInParameter.RBS_CURVE_HEIGHT_PARAM,
        BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
        BuiltInParameter.RBS_PIPE_FLOW_PARAM,
        BuiltInParameter.RBS_DUCT_FLOW_PARAM,
        BuiltInParameter.RBS_PIPE_SLOPE,
        BuiltInParameter.RBS_CURVE_SLOPE
    };

    private static readonly BuiltInParameter[] MepPart =
    {
        BuiltInParameter.RBS_SYSTEM_NAME_PARAM,
        BuiltInParameter.RBS_SYSTEM_ABBREVIATION_PARAM,
        BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
        BuiltInParameter.RBS_CURVE_DIAMETER_PARAM,
        BuiltInParameter.RBS_CURVE_WIDTH_PARAM,
        BuiltInParameter.RBS_CURVE_HEIGHT_PARAM,
        BuiltInParameter.ALL_MODEL_MANUFACTURER,
        BuiltInParameter.ALL_MODEL_MODEL
    };

    private static readonly BuiltInParameter[] Wall =
    {
        BuiltInParameter.WALL_ATTR_WIDTH_PARAM,
        BuiltInParameter.CURVE_ELEM_LENGTH,
        BuiltInParameter.WALL_ATTR_HEIGHT_PARAM
    };

    private static readonly BuiltInParameter[] Opening =
    {
        BuiltInParameter.FAMILY_WIDTH_PARAM,
        BuiltInParameter.FAMILY_HEIGHT_PARAM,
        BuiltInParameter.FAMILY_THICKNESS_PARAM,
        BuiltInParameter.DOOR_WIDTH,
        BuiltInParameter.DOOR_HEIGHT,
        BuiltInParameter.WINDOW_WIDTH,
        BuiltInParameter.WINDOW_HEIGHT
    };

    private static readonly BuiltInParameter[] Room =
    {
        BuiltInParameter.ROOM_NUMBER,
        BuiltInParameter.ROOM_NAME,
        BuiltInParameter.ROOM_AREA,
        BuiltInParameter.ROOM_VOLUME
    };

    internal static IReadOnlyList<ParameterValueInfo> Extract(
        Document doc,
        Element element,
        ElementType? type,
        bool elementIsType)
    {
        if (element is View view && !elementIsType)
            return ViewEssence(view);

        // A placed tag's essence is what it renders (TagText) and what it
        // points at — the read-back for "did the marks survive the parameter
        // migration" (the label→parameter link itself is not readable via the
        // API before Revit 2027, CF-3799; get_family_labels states that).
        if (element is IndependentTag tag && !elementIsType)
        {
            var tagEssence = TagEssence(tag);
            if (tagEssence.Count > 0)
                return tagEssence;
        }

        var profile = ProfileFor(element.Category);
        var result = new List<ParameterValueInfo>(Math.Min(profile.Length, MaxEntries));
        var seen = new HashSet<BuiltInParameter>();
        var seenValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var builtInParameter in profile)
        {
            if (!seen.Add(builtInParameter))
                continue;

            var parameter = element.get_Parameter(builtInParameter);
            var owner = element;
            var scope = elementIsType ? "type" : "instance";

            if (!elementIsType
                && parameter?.Element is { } parameterOwner
                && parameterOwner.Id != element.Id)
            {
                owner = parameterOwner;
                scope = "type";
            }

            if (!AgentParameterClassifier.IsMeaningful(parameter))
            {
                if (elementIsType || type is null)
                    continue;

                parameter = type.get_Parameter(builtInParameter);
                owner = type;
                scope = "type";
            }

            if (!AgentParameterClassifier.IsMeaningful(parameter))
                continue;

            var info = AgentParameterValueReader.Create(doc, parameter!, scope);
            var key = info.Name + "|" + info.Scope + "|" + info.Value;
            if (!seenValues.Add(key))
                continue;

            result.Add(info);
            if (result.Count >= MaxEntries)
                break;
        }

        return result;
    }

    /// <summary>
    /// A view's "essence" is not a parameter list: detail level and display
    /// style are the two answers to "why does this 3D view look schematic?".
    /// get_element_details returned neither, so the owner had to guess
    /// (feedback 2026-11-24).
    /// </summary>
    private static IReadOnlyList<ParameterValueInfo> ViewEssence(View view)
    {
        var result = new List<ParameterValueInfo>(4);
        AddViewValue(result, "Detail Level", SafeDetailLevel(view), "Integer");
        AddViewValue(result, "Display Style", SafeDisplayStyle(view), "Integer");
        try
        {
            if (view.Scale > 0)
                AddViewValue(result, "Scale", "1:" + view.Scale, "Integer");
        }
        catch (Exception)
        {
            // A view type without a scale: not an answer.
        }

        try
        {
            var templateId = view.ViewTemplateId;
            AddViewValue(
                result,
                "View Template",
                templateId == ElementId.InvalidElementId
                    ? "None"
                    : view.Document.GetElement(templateId)?.Name,
                "String");
        }
        catch (Exception)
        {
            // Template resolution is optional context.
        }

        return result;
    }

    private static IReadOnlyList<ParameterValueInfo> TagEssence(IndependentTag tag)
    {
        var result = new List<ParameterValueInfo>(2);
        try
        {
            // TagText throws when the tag has no tag behavior; HasTagText()
            // does not exist in the 2023/2024 API (net48 builds).
            AddViewValue(result, "Tag Text", tag.TagText, "String", "tag");
        }
        catch (Exception)
        {
            // The tag has no tag behavior in this context.
        }

        try
        {
            var tagged = tag.GetTaggedLocalElementIds();
            if (tagged.Count > 0)
            {
                AddViewValue(
                    result,
                    "Tagged Element Ids",
                    string.Join(", ", tagged.Select(id => id.GetValue().ToString(
                        System.Globalization.CultureInfo.InvariantCulture))),
                    "ElementId",
                    "tag");
            }
        }
        catch (Exception)
        {
            // Tagged references are optional context.
        }

        return result;
    }

    private static string? SafeDetailLevel(View view)
    {
        try
        {
            return view.HasDetailLevel() ? view.DetailLevel.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    private static void AddViewValue(
        List<ParameterValueInfo> target,
        string name,
        string? value,
        string storageType,
        string scope = "view")
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        target.Add(new ParameterValueInfo
        {
            Name = name,
            Value = value,
            DisplayValue = value,
            StorageType = storageType,
            ParameterClass = "system",
            Scope = scope
        });
    }

    internal static BuiltInParameter[] ProfileFor(Category? category)
    {
        if (category is null)
            return Common;

        var id = category.Id.GetValue();
        if (id is < int.MinValue or > int.MaxValue)
            return Common;

        switch ((BuiltInCategory)(int)id)
        {
            case BuiltInCategory.OST_PipeCurves:
            case BuiltInCategory.OST_DuctCurves:
            case BuiltInCategory.OST_Conduit:
            case BuiltInCategory.OST_CableTray:
            case BuiltInCategory.OST_FlexPipeCurves:
            case BuiltInCategory.OST_FlexDuctCurves:
                return MepCurve;

            case BuiltInCategory.OST_PipeFitting:
            case BuiltInCategory.OST_DuctFitting:
            case BuiltInCategory.OST_ConduitFitting:
            case BuiltInCategory.OST_CableTrayFitting:
            case BuiltInCategory.OST_PipeAccessory:
            case BuiltInCategory.OST_DuctAccessory:
            case BuiltInCategory.OST_MechanicalEquipment:
            case BuiltInCategory.OST_ElectricalEquipment:
            case BuiltInCategory.OST_PlumbingFixtures:
            case BuiltInCategory.OST_Sprinklers:
                return MepPart;

            case BuiltInCategory.OST_Walls:
                return Wall;

            case BuiltInCategory.OST_Doors:
            case BuiltInCategory.OST_Windows:
                return Opening;

            case BuiltInCategory.OST_Rooms:
            case BuiltInCategory.OST_MEPSpaces:
                return Room;

            default:
                return Common;
        }
    }
}