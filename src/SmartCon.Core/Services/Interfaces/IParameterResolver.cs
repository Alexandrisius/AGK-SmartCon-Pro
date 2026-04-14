using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Determines which parameter controls the connector size and changes its value.
/// Implementation: SmartCon.Revit/Parameters/RevitParameterResolver.cs
/// </summary>
public interface IParameterResolver
{
    /// <summary>
    /// Parameters that the connector radius depends on.
    /// </summary>
    IReadOnlyList<ParameterDependency> GetConnectorRadiusDependencies(
        Document doc, ElementId elementId, int connectorIndex);

    /// <summary>
    /// Set the target radius by changing dependent parameters.
    /// Returns true on success.
    /// </summary>
    bool TrySetConnectorRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits);

    /// <summary>
    /// Selects a fitting type size so that:
    /// 1. The connector on the static element side (staticConnIdx) exactly matches staticRadius.
    /// 2. Among matching types, the dynamic side connector (dynConnIdx) is as close as possible to dynRadius.
    /// If no exact static match exists, applies the type with minimum static deviation (fallback).
    /// Returns: StaticExact=true if exact match found; AchievedDynRadius — actual
    /// dynConn radius after type change (may differ from dynRadius).
    /// Call INSIDE transaction.
    /// </summary>
    (bool StaticExact, double AchievedDynRadius) TrySetFittingTypeForPair(
        Document doc, ElementId fittingId,
        int staticConnIdx, double staticRadius,
        int dynConnIdx, double dynRadius);
}
