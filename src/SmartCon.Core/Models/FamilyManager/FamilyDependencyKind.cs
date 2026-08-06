namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Discriminator values for <see cref="FamilyDependencyInfo.Kind"/> stored in
/// <c>family_dependencies.dependency_kind</c> (ADR-066). String constants
/// (not an enum) because the column is a plain TEXT discriminated union and
/// new kinds must not require a schema change.
/// </summary>
public static class FamilyDependencyKind
{
    /// <summary>
    /// Fitting family referenced by a system MEPCurve type's
    /// RoutingPreferenceManager rule (pipes / ducts / conduits / cable trays).
    /// <see cref="FamilyDependencyInfo.PartName"/> carries the original
    /// "Family:Type" token of the rule.
    /// </summary>
    public const string Routing = "routing";

    /// <summary>
    /// Shared nested family declared by a loadable parent family (E2, #209).
    /// </summary>
    public const string SharedNested = "shared_nested";
}
