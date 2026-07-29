using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Pure validation engine: evaluates a family's normalized parameter
/// values against a category's effective validation rules. No Revit API,
/// no database — fully unit-testable. Rules are checked per family type;
/// one failing type fails the family (hard gate semantics).
/// </summary>
public interface IFamilyValidationEngine
{
    FamilyValidationReport Validate(FamilyValidationInput input, IReadOnlyList<EffectiveValidationRule> rules);
}
