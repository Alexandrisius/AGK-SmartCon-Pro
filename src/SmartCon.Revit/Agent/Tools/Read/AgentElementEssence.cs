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

            var info = AgentParameterValueReader.Create(doc, owner, parameter!, scope);
            var key = info.Name + "|" + info.Scope + "|" + info.Value;
            if (!seenValues.Add(key))
                continue;

            result.Add(info);
            if (result.Count >= MaxEntries)
                break;
        }

        return result;
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