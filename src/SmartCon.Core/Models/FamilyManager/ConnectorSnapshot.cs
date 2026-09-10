namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Snapshot of a single <c>ConnectorElement</c> inside a family document
/// (ADR-056, Issue #159). Connectors carry MEP identity that parameters
/// do not: domain, profile, sizes, system classification and intra-family
/// linkage. All enum values are raw ordinals (I-09 — Core must not
/// reference Revit runtime types; same rule as ADR-055 facts).
/// </summary>
/// <param name="Domain"><c>Domain</c> enum ordinal (HVAC, Piping,
/// Electrical, CableTrayConduit, …).</param>
/// <param name="Shape"><c>ConnectorProfileType</c> enum ordinal
/// (Round, Rectangular, Oval).</param>
/// <param name="SystemClassification"><c>MEPSystemClassification</c>
/// enum ordinal (SupplyAir, DomesticColdWater, …).</param>
/// <param name="IsPrimary"><c>true</c> for the primary connector of the
/// family.</param>
/// <param name="Width">Connector width in internal units (feet), or
/// <c>null</c> when the profile has no width dimension.</param>
/// <param name="Height">Connector height in internal units (feet), or
/// <c>null</c> when not applicable.</param>
/// <param name="Radius">Connector radius in internal units (feet), or
/// <c>null</c> when not applicable.</param>
/// <param name="OriginX">Connector origin X in internal units (feet),
/// family-local coordinates. Rounded to 1e-4 ft by the hasher.</param>
/// <param name="OriginY">Connector origin Y.</param>
/// <param name="OriginZ">Connector origin Z.</param>
/// <param name="LinkedIndex">Index of the linked connector inside the
/// same sorted connector list (intra-family link from
/// <c>ConnectorElement.GetLinkedConnectorElement()</c>), or <c>-1</c>
/// when the connector is not linked. Captures connection topology
/// (e.g. fitting connector pairs).</param>
public sealed record ConnectorSnapshot(
    int Domain,
    int Shape,
    int SystemClassification,
    bool IsPrimary,
    double? Width,
    double? Height,
    double? Radius,
    double OriginX,
    double OriginY,
    double OriginZ,
    int LinkedIndex);
