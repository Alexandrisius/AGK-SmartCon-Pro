namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Kind of the data source an auto-assignment condition (#241) compares
/// against: a library attribute (parameter name match on family types) or
/// a system field (built-in family-level value such as the Revit category).
/// </summary>
public enum AssignmentConditionSourceKind
{
    /// <summary>Library attribute (<c>attribute_definitions</c>), matched
    /// by parameter name against family type values.</summary>
    Attribute = 0,

    /// <summary>Built-in system field (Revit category ordinal, Part Type
    /// fact, family name, system family key).</summary>
    System = 1,
}
