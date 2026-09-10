namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// A validation rule resolved against a category's effective attribute set
/// (direct + inherited bindings) with the attribute display context the
/// rule itself does not carry.
/// </summary>
/// <param name="AttributeName">Attribute (parameter) name the rule
/// targets — matched against parameter names on family types.</param>
/// <param name="IsInherited"><c>true</c> when the binding (and therefore
/// the rule) comes from an ancestor category.</param>
/// <param name="Rule">The rule definition from the catalog database.</param>
public sealed record EffectiveValidationRule(
    string AttributeName,
    bool IsInherited,
    ValidationRule Rule);
