using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Revit.Agent.Tools.Read;

internal static class AgentElementDetailsBuilder
{
    internal static ElementDetails BuildElement(
        Document doc,
        Element element,
        ElementType? type,
        AgentParameterFilter filter)
    {
        var card = AgentElementCardFactory.Create(doc, element);
        var isTypeElement = element is ElementType;
        var kind = isTypeElement
            ? "type"
            : element is FamilyInstance ? "instance" : "element";
        var typeId = isTypeElement
            ? ((ElementType)element).Id.GetValue()
            : card.TypeId;

        IReadOnlyList<ParameterValueInfo> instanceParameters = Array.Empty<ParameterValueInfo>();
        var instanceTotal = 0;
        var instanceTruncated = false;
        var familyOmitted = 0;

        if (!isTypeElement)
        {
            instanceParameters = AgentParameterCollector.Collect(
                doc,
                element,
                element.Parameters,
                "instance",
                filter,
                out instanceTotal,
                out instanceTruncated,
                out familyOmitted);
        }

        return new ElementDetails
        {
            UniqueId = card.UniqueId,
            ElementId = card.ElementId,
            Kind = kind,
            TypeId = typeId,
            FamilyId = card.FamilyId,
            FamilyUniqueId = card.FamilyUniqueId,
            FamilyKind = ResolveFamilyKind(element, type),
            Category = card.Category,
            Family = card.Family,
            Type = card.Type,
            Name = card.Name,
            LevelId = card.LevelId,
            LevelName = card.LevelName,
            Room = card.Room,
            Essence = AgentElementEssence.Extract(doc, element, type, isTypeElement),
            InstanceParameters = instanceParameters,
            InstanceParametersTotal = instanceTotal,
            InstanceParametersReturned = instanceParameters.Count,
            InstanceParametersTruncated = instanceTruncated,
            FamilyParametersOmitted = familyOmitted
        };
    }

    internal static ElementTypeDetails BuildType(
        Document doc,
        ElementType type,
        AgentParameterFilter filter,
        bool includeTypeParameters)
    {
        var card = AgentElementCardFactory.Create(doc, type);

        IReadOnlyList<ParameterValueInfo>? parameters = null;
        int? total = null;
        int? returned = null;
        bool? truncated = null;
        int familyOmitted;

        if (includeTypeParameters)
        {
            parameters = AgentParameterCollector.Collect(
                doc,
                type,
                type.Parameters,
                "type",
                filter,
                out var collectedTotal,
                out var collectedTruncated,
                out familyOmitted);
            total = collectedTotal;
            returned = parameters.Count;
            truncated = collectedTruncated;
        }
        else
        {
            familyOmitted = AgentParameterCollector.CountFamilyOmitted(type.Parameters, filter);
        }

        return new ElementTypeDetails
        {
            TypeId = type.Id.GetValue(),
            UniqueId = type.UniqueId,
            Name = type.Name,
            FamilyName = type is FamilySymbol symbol ? symbol.Family?.Name : type.FamilyName,
            FamilyId = card.FamilyId,
            FamilyUniqueId = card.FamilyUniqueId,
            FamilyKind = ResolveFamilyKind(type, type),
            Category = card.Category,
            Essence = AgentElementEssence.Extract(doc, type, null, true),
            Parameters = parameters,
            ParametersTotal = total,
            ParametersReturned = returned,
            ParametersTruncated = truncated,
            FamilyParametersOmitted = familyOmitted
        };
    }

    private static string? ResolveFamilyKind(Element element, ElementType? type)
    {
        var symbol = element as FamilySymbol
                     ?? (element as FamilyInstance)?.Symbol
                     ?? type as FamilySymbol;

        if (symbol is not null)
            return symbol.Family?.IsInPlace == true ? "in-place" : "loadable";

        if (element is FamilyInstance)
            return "loadable";

        return type is null ? null : "system";
    }
}