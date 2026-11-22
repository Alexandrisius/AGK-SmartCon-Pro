using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Общие читалки атрибутов для read-тулов связей (get_element_relations,
/// trace_system_chain, get_room_contents): категория, марка, уровень,
/// имя системы (RBS_SYSTEM_NAME_PARAM), семейство/тип, ссылка на помещение.
/// </summary>
internal static class AgentRelationsSupport
{
    /// <summary>Марка (ALL_MODEL_MARK) элемента, null когда пустая.</summary>
    internal static string? ReadMark(Element element) =>
        ReadString(element, BuiltInParameter.ALL_MODEL_MARK);

    /// <summary>
    /// Системное имя MEP-элемента (RBS_SYSTEM_NAME_PARAM) — читается с элемента,
    /// а не из свойств MEPSystem: значение едино с UI и одинаково у труб,
    /// фитингов и оборудования системы.
    /// </summary>
    internal static string? ReadSystemName(Element element) =>
        ReadString(element, BuiltInParameter.RBS_SYSTEM_NAME_PARAM);

    internal static string? ReadLevelName(Document doc, Element element)
    {
        var levelId = element.LevelId;
        if (levelId is null || levelId == ElementId.InvalidElementId)
            return null;

        var name = doc.GetElement(levelId)?.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    internal static (string? Family, string? Type) ReadFamilyAndType(Element element)
    {
        if (element is FamilyInstance instance)
            return (instance.Symbol?.Family?.Name, instance.Symbol?.Name);

        if (element is ElementType elementType)
            return (elementType.FamilyName, elementType.Name);

        var typeId = element.GetTypeId();
        if (typeId != ElementId.InvalidElementId
            && element.Document.GetElement(typeId) is ElementType type)
        {
            return (type.FamilyName, type.Name);
        }

        return (null, element.Name);
    }

    /// <summary>
    /// Компактная ссылка на помещение: id, номер, имя. ROOM_NAME берётся из
    /// параметра (Room.Name склеивает имя и номер, Autodesk Developer Guide).
    /// </summary>
    internal static object? RoomReference(Room? room) =>
        room is null ? null : new
        {
            roomId = room.Id.GetValue(),
            number = ReadString(room, BuiltInParameter.ROOM_NUMBER) ?? room.Number,
            name = ReadString(room, BuiltInParameter.ROOM_NAME) ?? room.Name
        };

    /// <summary>
    /// ConnectorManager элемента: MEPCurve несёт свой, FamilyInstance — через
    /// MEPModel (паттерн ElementChainIterator.GetConnectorManager).
    /// </summary>
    internal static ConnectorManager? GetConnectorManager(Element? element) =>
        element switch
        {
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            MEPCurve mc => mc.ConnectorManager,
            _ => null
        };

    internal static string? ReadString(Element element, BuiltInParameter parameter)
    {
        try
        {
            var value = element.get_Parameter(parameter)?.AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
