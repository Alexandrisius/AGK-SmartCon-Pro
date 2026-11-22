using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Compatibility;
using SmartCon.Core;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Read-тур состава помещения (кейс 10): элементы комнаты по семействам
/// с количеством. Room-bound FamilyInstance — через instance.Room (и
/// FromRoom/ToRoom дверей/окон), не room-bound (трубы и пр.) — по центру
/// bbox внутри габарита помещения. Read-only.
/// </summary>
public sealed class GetRoomContentsTool : AgentReadToolBase
{
    private const int DefaultLimit = 200;
    private const int HardLimit = 1000;
    private const int SampleSize = 5;

    /// <summary>ft² → m² (ROOM_AREA хранится в квадратных футах).</summary>
    private const double SquareFeetToSquareMeters = 0.09290304;

    /// <summary>Допуск попадания точки в габарит помещения, футы.</summary>
    private const double BoundingBoxTolerance = 1e-9;

    public GetRoomContentsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_room_contents",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Returns what stands in a room, grouped by family and type with counts: the room " +
            "card (id, number, name, area in m2) plus contents[] entries (family, type, " +
            "category, count, elementIds) sorted by count descending. The room is located by " +
            "roomId, roomName or roomNumber (one of them is required; several matches fail with " +
            "a candidate list). Room-bound family instances are collected via instance.Room, " +
            "doors and windows also via FromRoom/ToRoom; elements without a room assignment " +
            "(pipes and other curves) fall back to a bounding-box containment check. Rooms, " +
            "views, sheets and room tags are excluded; model groups are only expanded when " +
            "includeModelGroups=true. Read-only.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""roomId"":{""type"":""integer""},""roomName"":{""type"":""string""},""roomNumber"":{""type"":""string""},""includeModelGroups"":{""type"":""boolean"",""default"":false},""limit"":{""type"":""integer"",""minimum"":1,""maximum"":1000,""default"":200}},""additionalProperties"":false}"
    };

    private sealed class ContentGroup
    {
        public required string Key { get; init; }

        public required string? Family { get; init; }

        public required string? Type { get; init; }

        public required string? Category { get; init; }

        public List<long> ElementIds { get; } = new();
    }

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var hasRoomId = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("roomId", out var roomIdElement)
            && roomIdElement.ValueKind == JsonValueKind.Number
            && roomIdElement.TryGetInt64(out _);
        var roomName = AgentJson.GetString(args, "roomName");
        var roomNumber = AgentJson.GetString(args, "roomNumber");

        if (!hasRoomId && string.IsNullOrWhiteSpace(roomName) && string.IsNullOrWhiteSpace(roomNumber))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "One of roomId, roomName or roomNumber is required.");
        }

        var includeModelGroups = AgentJson.GetBool(args, "includeModelGroups", false);
        var limit = Math.Min(
            Math.Max(AgentJson.GetInt(args, "limit", DefaultLimit), 1),
            HardLimit);

        var room = FindRoom(doc, hasRoomId ? AgentJson.GetLong(args, "roomId", 0) : null, roomName, roomNumber,
            out var candidates);
        if (room is null)
        {
            return ToolResult.Fail(
                "room_not_found",
                "No room matches the given roomId/roomName/roomNumber in this document.",
                data: AgentJson.Serialize(new Dictionary<string, object?>
                {
                    ["candidates"] = candidates
                }));
        }

        if (room.Location is null)
        {
            return ToolResult.Fail(
                "room_unplaced",
                "The room is not placed (no enclosed region on its level), contents cannot be located.");
        }

        var roomBox = room.get_BoundingBox(null);
        if (roomBox is null)
        {
            return ToolResult.Fail(
                "room_unplaced",
                "The room has no bounding box, contents cannot be located.");
        }

        var elements = CollectElements(doc, room, roomBox, includeModelGroups, ct);
        var groups = GroupByFamilyAndType(doc, elements);

        var elementIds = new List<long>();
        var contents = new List<object>();
        foreach (var group in groups)
        {
            foreach (var id in group.ElementIds)
            {
                if (elementIds.Count < limit)
                    elementIds.Add(id);
            }

            contents.Add(new
            {
                family = group.Family,
                type = group.Type,
                category = group.Category,
                count = group.ElementIds.Count,
                sample = group.ElementIds.Take(SampleSize).ToArray()
            });
        }

        var elementsTotal = groups.Sum(group => group.ElementIds.Count);
        var payload = new
        {
            room = new
            {
                roomId = room.Id.GetValue(),
                number = AgentRelationsSupport.ReadString(room, BuiltInParameter.ROOM_NUMBER),
                name = AgentRelationsSupport.ReadString(room, BuiltInParameter.ROOM_NAME),
                areaM2 = room.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() * SquareFeetToSquareMeters
            },
            contents,
            elementIds,
            familiesTotal = groups.Count,
            elementsTotal,
            truncated = elementsTotal > elementIds.Count
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Room contains " + elementsTotal + " element(s) in " + groups.Count + " family/type group(s).");
    }

    private static Room? FindRoom(
        Document doc,
        long? roomId,
        string? roomName,
        string? roomNumber,
        out List<object> candidates)
    {
        candidates = new List<object>();
        if (roomId is not null)
            return doc.GetElement(ElementIdCompat.Create(roomId.Value)) as Room;

        var matches = new List<Room>();
        foreach (var element in new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType())
        {
            if (element is not Room candidate)
                continue;

            var name = AgentRelationsSupport.ReadString(candidate, BuiltInParameter.ROOM_NAME);
            var number = AgentRelationsSupport.ReadString(candidate, BuiltInParameter.ROOM_NUMBER);
            var nameMatches = !string.IsNullOrWhiteSpace(roomName)
                && string.Equals(name, roomName, StringComparison.OrdinalIgnoreCase);
            var numberMatches = !string.IsNullOrWhiteSpace(roomNumber)
                && string.Equals(number, roomNumber, StringComparison.OrdinalIgnoreCase);
            if (nameMatches || numberMatches)
                matches.Add(candidate);
        }

        foreach (var candidate in matches.Take(20))
        {
            candidates.Add(new Dictionary<string, object?>
            {
                ["roomId"] = candidate.Id.GetValue(),
                ["name"] = AgentRelationsSupport.ReadString(candidate, BuiltInParameter.ROOM_NAME),
                ["number"] = AgentRelationsSupport.ReadString(candidate, BuiltInParameter.ROOM_NUMBER)
            });
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Сбор элементов помещения: room-bound через instance.Room, двери/окна
    /// дополнительно через FromRoom/ToRoom, остальное (MEPCurve и свободные
    /// FamilyInstance) — центром bbox внутри габарита комнаты. Комнаты, метки
    /// комнат, аннотации (view-specific) исключаются; model-группы при
    /// includeModelGroups=true раскрываются в члены (GetMemberIds).
    /// </summary>
    private static List<ElementId> CollectElements(
        Document doc,
        Room room,
        BoundingBoxXYZ roomBox,
        bool includeModelGroups,
        CancellationToken ct)
    {
        var result = new Dictionary<long, ElementId>();
        var roomId = room.Id.GetValue();

        foreach (var element in new FilteredElementCollector(doc)
            .WhereElementIsNotElementType())
        {
            ct.ThrowIfCancellationRequested();

            if (element is Group group)
            {
                // Группа сама не модель помещения: по флагу раскрывается в члены.
                if (includeModelGroups)
                {
                    foreach (var memberId in group.GetMemberIds())
                    {
                        if (doc.GetElement(memberId) is FamilyInstance memberInstance
                            && IsCollectableInstance(memberInstance)
                            && MatchesRoom(memberInstance, roomId))
                        {
                            result[memberId.GetValue()] = memberId;
                        }
                    }
                }

                continue;
            }

            if (element is not (FamilyInstance or MEPCurve))
                continue;
            if (element is FamilyInstance fi && !IsCollectableInstance(fi))
                continue;

            if (element is FamilyInstance instance && MatchesRoom(instance, roomId))
            {
                result[element.Id.GetValue()] = element.Id;
                continue;
            }

            // Не room-bound: центр bbox внутри габарита комнаты.
            var box = element.get_BoundingBox(null);
            if (box is null)
                continue;

            var center = (box.Min + box.Max) * 0.5;
            if (!IsInside(center, roomBox))
                continue;

            result[element.Id.GetValue()] = element.Id;
        }

        return result.Values.ToList();
    }

    private static bool IsCollectableInstance(FamilyInstance instance)
    {
        // Аннотации и виды-специфичные элементы — не состав помещения.
        if (instance.OwnerViewId != ElementId.InvalidElementId)
            return false;

        var category = instance.Category?.Id.GetValue();
        return category != (long)(int)BuiltInCategory.OST_RoomTags
            && category != (long)(int)BuiltInCategory.OST_Rooms;
    }

    /// <summary>
    /// Привязка FamilyInstance к комнате: instance.Room (фазо-зависимо, до 2024
    /// бросает InvalidOperationException — Autodesk docs) плюс FromRoom/ToRoom
    /// дверей и окон (граница помещений).
    /// </summary>
    private static bool MatchesRoom(FamilyInstance instance, long roomId)
    {
        try
        {
            if (instance.Room?.Id.GetValue() == roomId)
                return true;
        }
        catch (InvalidOperationException)
        {
            // Фаза не применяется к элементу — комнаты нет.
        }

        try
        {
            var category = instance.Category?.Id.GetValue();
            var isOpening = category == (long)(int)BuiltInCategory.OST_Doors
                         || category == (long)(int)BuiltInCategory.OST_Windows;
            if (isOpening
                && (instance.FromRoom?.Id.GetValue() == roomId
                    || instance.ToRoom?.Id.GetValue() == roomId))
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // Вне фазы — стороны не вычислены.
        }

        return false;
    }

    private static bool IsInside(XYZ point, BoundingBoxXYZ box) =>
        point.X >= box.Min.X - BoundingBoxTolerance
        && point.X <= box.Max.X + BoundingBoxTolerance
        && point.Y >= box.Min.Y - BoundingBoxTolerance
        && point.Y <= box.Max.Y + BoundingBoxTolerance
        && point.Z >= box.Min.Z - 1.0 // немного ниже пола — элементы уровня комнаты
        && point.Z <= box.Max.Z + BoundingBoxTolerance;

    private static List<ContentGroup> GroupByFamilyAndType(Document doc, List<ElementId> elements)
    {
        var groups = new Dictionary<string, ContentGroup>(StringComparer.Ordinal);
        foreach (var id in elements)
        {
            var element = doc.GetElement(id);
            if (element is null)
                continue;

            var (family, typeName) = AgentRelationsSupport.ReadFamilyAndType(element);
            var category = AgentElementCardFactory.GetCategoryName(element.Category);
            var key = (family ?? "<none>") + "|" + (typeName ?? "<none>") + "|" + (category ?? "<none>");
            if (!groups.TryGetValue(key, out var group))
            {
                group = new ContentGroup
                {
                    Key = key,
                    Family = family,
                    Type = typeName,
                    Category = category
                };
                groups[key] = group;
            }

            group.ElementIds.Add(id.GetValue());
        }

        return groups.Values
            .OrderByDescending(group => group.ElementIds.Count)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
    }
}
