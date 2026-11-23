using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Read-тур связей элемента (кейс 10 real-cases benchmark v2): соединения
/// по коннекторам (AllRefs →.Owner), хост FamilyInstance, from/to помещения
/// дверей и окон, уровень и имя системы — один вход, все типы связей.
/// Read-only, без транзакций и регенерации.
/// </summary>
public sealed class GetElementRelationsTool : AgentReadToolBase
{
    private const int MaxElements = 10;

    public GetElementRelationsTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_element_relations",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Reads all relation types of up to 10 elements in one call: physical pipe/duct " +
            "connections (each connector with origin in mm, radius in mm, free/connected state " +
            "and neighbour cards with elementId, category, family, type, mark), the host element " +
            "(FamilyInstance.Host — e.g. which wall hosts a window), fromRoom/toRoom for doors " +
            "and windows (reported separately, null when not applicable), plus level and system " +
            "name context. Neighbours come from Connector.AllRefs; logical system references " +
            "and logical references are skipped, connectors are sorted by id for deterministic " +
            "output. An element without connectors returns connectors:[] plus connectorsNote " +
            "with the reason (no MEP connectors / family defines none / curve-only). Read-only.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""elementIds"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":10},""includeConnectors"":{""type"":""boolean"",""default"":true},""includeHost"":{""type"":""boolean"",""default"":true},""includeRooms"":{""type"":""boolean"",""default"":true}},""required"":[""elementIds""],""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var elementIds = AgentJson.GetLongArray(args, "elementIds");
        if (elementIds.Count == 0)
            return ToolResult.Fail("invalid_arguments", "elementIds must contain at least one id.");

        if (elementIds.Count > MaxElements)
            return ToolResult.Fail(
                "too_many_elements",
                "At most " + MaxElements + " element ids are allowed per call.");

        var includeConnectors = AgentJson.GetBool(args, "includeConnectors", true);
        var includeHost = AgentJson.GetBool(args, "includeHost", true);
        var includeRooms = AgentJson.GetBool(args, "includeRooms", true);

        var relations = new List<object>();
        var missing = new List<long>();

        foreach (var id in elementIds)
        {
            ct.ThrowIfCancellationRequested();

            var element = doc.GetElement(ElementIdCompat.Create(id));
            if (element is null)
            {
                missing.Add(id);
                continue;
            }

            relations.Add(BuildRelation(doc, element, includeConnectors, includeHost, includeRooms));
        }

        if (relations.Count == 0)
        {
            return ToolResult.Fail(
                "element_not_found",
                "None of the requested element ids exist in this document.");
        }

        var payload = new
        {
            total = relations.Count,
            missing,
            relations
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Returned relations for " + relations.Count + " element(s).");
    }

    private static object BuildRelation(
        Document doc,
        Element element,
        bool includeConnectors,
        bool includeHost,
        bool includeRooms)
    {
        var (family, typeName) = AgentRelationsSupport.ReadFamilyAndType(element);
        var instance = element as FamilyInstance;

        var connectors = includeConnectors ? ReadConnectors(element) : null;

        // An empty list alone reads as "nothing is connected"; the reason says the
        // element can never have a connection (C10-02).
        var connectorsNote = connectors is { Count: 0 } ? AgentConnectorNotes.DescribeAbsence(element) : null;

        return new
        {
            elementId = element.Id.GetValue(),
            category = AgentElementCardFactory.GetCategoryName(element.Category),
            name = element.Name,
            family,
            type = typeName,
            mark = AgentRelationsSupport.ReadMark(element),
            levelName = AgentRelationsSupport.ReadLevelName(doc, element),
            systemName = AgentRelationsSupport.ReadSystemName(element),
            host = includeHost ? ReadHost(doc, instance) : null,
            fromRoom = includeRooms ? ReadRoomSide(instance, from: true) : null,
            toRoom = includeRooms ? ReadRoomSide(instance, from: false) : null,
            connectors,
            connectorsNote
        };
    }

    /// <summary>
    /// Host элемент (окно/дверь → стена; null для не-hosted: трубы, мебель).
    /// FamilyInstance.Host возвращает содержащий элемент (Developer Guide).
    /// </summary>
    private static object? ReadHost(Document doc, FamilyInstance? instance)
    {
        var host = instance?.Host;
        if (host is null)
            return null;

        return new
        {
            elementId = host.Id.GetValue(),
            category = AgentElementCardFactory.GetCategoryName(host.Category),
            name = host.Name
        };
    }

    /// <summary>
    /// FromRoom/ToRoom двери или окна: считаются Revit по фазам; до 2024 API
    /// бросает InvalidOperationException когда фаза не применяется — null.
    /// Стороны возвращаются РАЗДЕЛЬНО (FromRoom / ToRoom), не слитой строкой.
    /// </summary>
    private static object? ReadRoomSide(FamilyInstance? instance, bool from)
    {
        if (instance is null)
            return null;

        try
        {
            var category = instance.Category?.Id.GetValue();
            var isOpening = category == (long)(int)BuiltInCategory.OST_Doors
                         || category == (long)(int)BuiltInCategory.OST_Windows;
            if (!isOpening)
                return null;

            return AgentRelationsSupport.RoomReference(from ? instance.FromRoom : instance.ToRoom);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Коннекторы элемента с соседями: физические связи через AllRefs → Owner.
    /// Пропускаются: Curve-коннекторы (I-08), ссылки на самого себя и
    /// логические ссылки на MEPSystem (Autodesk Connector example).
    /// Сортировка по Id — детерминированный порядок.
    /// </summary>
    private static List<object> ReadConnectors(Element element)
    {
        var result = new List<object>();
        var manager = AgentRelationsSupport.GetConnectorManager(element);
        if (manager is null)
            return result;

        var connectors = new List<Connector>();
        foreach (Connector conn in manager.Connectors)
        {
            if (conn.ConnectorType == ConnectorType.Curve) // I-08
                continue;
            connectors.Add(conn);
        }

        connectors.Sort((a, b) => a.Id.CompareTo(b.Id));

        foreach (var conn in connectors)
        {
            var neighbors = new List<object>();
            if (conn.IsConnected)
            {
                var neighbourIds = new SortedSet<long>();
                foreach (Connector reference in conn.AllRefs)
                {
                    var owner = reference.Owner;
                    if (owner is null || owner is MEPSystem)
                        continue;
                    if (owner.Id == element.Id)
                        continue;
                    if (reference.ConnectorType == ConnectorType.Curve) // I-08
                        continue;

                    neighbourIds.Add(owner.Id.GetValue());
                }

                foreach (var neighbourId in neighbourIds)
                {
                    var neighbour = element.Document.GetElement(ElementIdCompat.Create(neighbourId));
                    if (neighbour is null)
                        continue;

                    var (family, typeName) = AgentRelationsSupport.ReadFamilyAndType(neighbour);
                    neighbors.Add(new
                    {
                        elementId = neighbourId,
                        category = AgentElementCardFactory.GetCategoryName(neighbour.Category),
                        family,
                        type = typeName,
                        mark = AgentRelationsSupport.ReadMark(neighbour),
                        name = neighbour.Name
                    });
                }
            }

            result.Add(new
            {
                id = conn.Id,
                domain = conn.Domain.ToString(),
                originMm = new
                {
                    x = conn.Origin.X * SmartCon.Core.Units.FeetToMm,
                    y = conn.Origin.Y * SmartCon.Core.Units.FeetToMm,
                    z = conn.Origin.Z * SmartCon.Core.Units.FeetToMm
                },
                radiusMm = conn.Radius * SmartCon.Core.Units.FeetToMm,
                isFree = !conn.IsConnected,
                isConnected = conn.IsConnected,
                neighbors
            });
        }

        return result;
    }
}
