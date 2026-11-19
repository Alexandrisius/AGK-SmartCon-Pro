using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Agent;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>Request payload for a single family reference.</summary>
internal sealed class FamilyRef
{
    public long? FamilyId { get; set; }

    public string? FamilyName { get; set; }
}

/// <summary>Parameter reference used by write tools.</summary>
internal sealed class FamilyParameterRef
{
    public string? Name { get; set; }

    /// <summary>instance | type; optional, disambiguates a same-name parameter.</summary>
    public string? Scope { get; set; }

    public string? Guid { get; set; }
}

internal sealed class RenameFamilyParameterRequest
{
    public long? FamilyId { get; set; }

    public string? FamilyName { get; set; }

    public string? Parameter { get; set; }

    public string? NewName { get; set; }

    public string? Scope { get; set; }

    public string? Guid { get; set; }
}

internal sealed class AddFamilyParameterRequest
{
    public long? FamilyId { get; set; }

    public string? FamilyName { get; set; }

    public string? Name { get; set; }

    public string? StorageType { get; set; }

    public string? Group { get; set; }

    public bool IsInstance { get; set; }

    public bool IsShared { get; set; }

    public string? Guid { get; set; }

    /// <summary>Optional initial value applied to the current type only.</summary>
    public string? InitialValue { get; set; }

    public string? Unit { get; set; }
}

internal sealed class FamilyValueItem
{
    public string? Parameter { get; set; }

    public string? Scope { get; set; }

    public string? Guid { get; set; }

    public string? TypeName { get; set; }

    public long? TypeId { get; set; }

    public string? Value { get; set; }

    public string? Unit { get; set; }
}

internal sealed class SetFamilyParameterValuesRequest
{
    public long? FamilyId { get; set; }

    public string? FamilyName { get; set; }

    public List<FamilyValueItem>? Items { get; set; }
}

internal sealed class SetFamilyFormulaRequest
{
    public long? FamilyId { get; set; }

    public string? FamilyName { get; set; }

    public string? Parameter { get; set; }

    public string? Scope { get; set; }

    public string? Guid { get; set; }

    /// <summary>Formula text; an empty string or null clears the formula.</summary>
    public string? Formula { get; set; }
}

internal sealed class MapParameterRef
{
    public string? Name { get; set; }

    public string? Scope { get; set; }

    public string? Guid { get; set; }
}

internal sealed class MapFamilyMapping
{
    public MapParameterRef? Source { get; set; }

    public MapParameterRef? Target { get; set; }

    /// <summary>values (default) | formula</summary>
    public string? Mode { get; set; }

    /// <summary>Optional type filter; omitted = all types.</summary>
    public List<string>? TypeNames { get; set; }
}

internal sealed class MapFamilyParametersRequest
{
    public long? FamilyId { get; set; }

    public string? FamilyName { get; set; }

    public List<MapFamilyMapping>? Mappings { get; set; }
}

/// <summary>Per-type value row of a family parameter report.</summary>
internal sealed class FamilyTypeValueReport
{
    public string? TypeName { get; set; }

    public string? Value { get; set; }

    public bool HasValue { get; set; }
}

/// <summary>Parameter card used by get_family_parameters.</summary>
internal sealed class FamilyParameterReport
{
    public string? Name { get; set; }

    public string? Scope { get; set; }

    public bool IsInstance { get; set; }

    public bool IsShared { get; set; }

    public string? Guid { get; set; }

    public string? StorageType { get; set; }

    public string? Spec { get; set; }

    public string? Group { get; set; }

    public string? Formula { get; set; }

    public bool IsDeterminedByFormula { get; set; }

    public List<string>? LookupTables { get; set; }

    public bool CanSetValue { get; set; }

    public bool CanSetFormula { get; set; }

    public bool IsReporting { get; set; }

    public bool IsReadOnly { get; set; }

    public List<FamilyTypeValueReport>? ValuesByType { get; set; }

    public bool ValuesTruncated { get; set; }
}

/// <summary>
/// Expected post-apply state read back from the project after LoadFamily.
/// </summary>
internal sealed class FamilyReadBackExpectation
{
    public required string TypeName { get; init; }

    public required string ParameterName { get; init; }

    public bool IsTypeParameter { get; init; } = true;

    /// <summary>Only check that the parameter exists on the type.</summary>
    public bool ExistsOnly { get; init; }

    public StorageType StorageType { get; init; }

    public double DoubleValue { get; init; }

    public int IntegerValue { get; init; }

    public string? TextValue { get; init; }

    public long ElementIdValue { get; init; }
}

/// <summary>Typed snapshot of one family parameter value.</summary>
internal readonly struct FamilyValueSnapshot
{
    public StorageType StorageType { get; init; }

    public double DoubleValue { get; init; }

    public int IntegerValue { get; init; }

    public string? TextValue { get; init; }

    public long ElementIdValue { get; init; }

    public static FamilyValueSnapshot ReadFamilyType(FamilyType? type, FamilyParameter parameter)
    {
        if (type is null)
            return default;

        switch (parameter.StorageType)
        {
            case StorageType.Double:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.Double,
                    DoubleValue = type.AsDouble(parameter) ?? 0.0
                };
            case StorageType.Integer:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.Integer,
                    IntegerValue = type.AsInteger(parameter) ?? 0
                };
            case StorageType.String:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.String,
                    TextValue = type.AsString(parameter)
                };
            case StorageType.ElementId:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.ElementId,
                    ElementIdValue = type.AsElementId(parameter)?.GetValue() ?? -1
                };
            default:
                return default;
        }
    }

    public static FamilyValueSnapshot ReadProjectParameter(Parameter? parameter)
    {
        if (parameter is null)
            return default;

        switch (parameter.StorageType)
        {
            case StorageType.Double:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.Double,
                    DoubleValue = parameter.AsDouble()
                };
            case StorageType.Integer:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.Integer,
                    IntegerValue = parameter.AsInteger()
                };
            case StorageType.String:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.String,
                    TextValue = parameter.AsString()
                };
            case StorageType.ElementId:
                return new FamilyValueSnapshot
                {
                    StorageType = StorageType.ElementId,
                    ElementIdValue = parameter.AsElementId()?.GetValue() ?? -1
                };
            default:
                return default;
        }
    }

    public FamilyReadBackExpectation ToExpectation(string typeName, string parameterName) => new()
    {
        TypeName = typeName,
        ParameterName = parameterName,
        StorageType = StorageType,
        DoubleValue = DoubleValue,
        IntegerValue = IntegerValue,
        TextValue = TextValue,
        ElementIdValue = ElementIdValue
    };

    public string ToRaw() => StorageType switch
    {
        StorageType.Double => AgentFamilyUnits.ToInvariant(DoubleValue),
        StorageType.Integer => AgentFamilyUnits.ToInvariant(IntegerValue),
        StorageType.String => TextValue ?? string.Empty,
        StorageType.ElementId => AgentFamilyUnits.ToInvariant(ElementIdValue),
        _ => string.Empty
    };

    public bool Matches(FamilyReadBackExpectation expectation, out string actual)
    {
        actual = ToRaw();
        if (StorageType != expectation.StorageType)
            return false;

        switch (StorageType)
        {
            case StorageType.Double:
                var tolerance = Math.Max(1e-9, Math.Abs(expectation.DoubleValue) * 1e-9);
                return Math.Abs(DoubleValue - expectation.DoubleValue) <= tolerance;
            case StorageType.Integer:
                return IntegerValue == expectation.IntegerValue;
            case StorageType.String:
                return string.Equals(TextValue ?? string.Empty, expectation.TextValue ?? string.Empty,
                    StringComparison.Ordinal);
            case StorageType.ElementId:
                return ElementIdValue == expectation.ElementIdValue;
            default:
                return true;
        }
    }
}

/// <summary>
/// Project-side snapshot of a family after LoadFamily: type name → symbol id.
/// </summary>
internal sealed class ProjectFamilySnapshot
{
    private readonly Dictionary<string, FamilySymbol> _symbols;

    private ProjectFamilySnapshot(Dictionary<string, FamilySymbol> symbols)
    {
        _symbols = symbols;
    }

    public int TypeCount => _symbols.Count;

    public static ProjectFamilySnapshot Capture(Document doc, string familyName)
    {
        var symbols = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
        var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
        foreach (var element in collector)
        {
            if (element is not FamilySymbol symbol)
                continue;

            var name = symbol.Family?.Name;
            if (string.IsNullOrEmpty(name)
                || !string.Equals(name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var typeName = symbol.Name;
            if (!string.IsNullOrEmpty(typeName) && !symbols.ContainsKey(typeName!))
                symbols.Add(typeName!, symbol);
        }

        return new ProjectFamilySnapshot(symbols);
    }

    public bool HasType(string typeName) => _symbols.ContainsKey(typeName);

    public bool TryRead(string typeName, string parameterName, out FamilyValueSnapshot value)
    {
        value = default;
        if (!_symbols.TryGetValue(typeName, out var symbol))
            return false;

        var parameter = symbol.LookupParameter(parameterName);
        if (parameter is null)
            return false;

        value = FamilyValueSnapshot.ReadProjectParameter(parameter);
        return true;
    }

    public bool HasParameter(string typeName, string parameterName)
    {
        if (!_symbols.TryGetValue(typeName, out var symbol))
            return false;

        return symbol.LookupParameter(parameterName) is not null;
    }
}

/// <summary>Parsed create request with resolved spec metadata.</summary>
internal sealed class AddFamilyParameterPlan
{
    public required string Name { get; init; }

    public required ForgeTypeId Spec { get; init; }

    public required ForgeTypeId Group { get; init; }

    public required AgentFamilyValueKind Kind { get; init; }

    public bool IsInstance { get; init; }

    public bool IsShared { get; init; }

    public Guid? Guid { get; init; }

    public string? InitialValue { get; init; }

    public string? Unit { get; init; }
}

internal static class AgentFamilyArgs
{
    internal static FamilyValueItem? FindItem(JsonElement args, string property) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize<FamilyValueItem>()
            : null;
}

/// <summary>Typed conversion/apply helpers shared by the family write tools.</summary>
internal static class AgentFamilyValues
{
    internal static FamilyValueSnapshot FromConverted(AgentFamilyConvertedValue converted) => new()
    {
        StorageType = converted.Kind switch
        {
            AgentFamilyValueKind.Number
                or AgentFamilyValueKind.Length or AgentFamilyValueKind.Angle
                or AgentFamilyValueKind.Area or AgentFamilyValueKind.Volume
                => StorageType.Double,
            AgentFamilyValueKind.WholeNumber or AgentFamilyValueKind.YesNo => StorageType.Integer,
            AgentFamilyValueKind.Text => StorageType.String,
            _ => StorageType.ElementId
        },
        DoubleValue = converted.DoubleValue,
        IntegerValue = converted.IntegerValue,
        TextValue = converted.TextValue,
        ElementIdValue = converted.ElementIdValue
    };

    internal static void Set(
        RevitFamilyManager manager,
        FamilyParameter parameter,
        FamilyValueSnapshot value)
    {
        switch (value.StorageType)
        {
            case StorageType.Double:
                manager.Set(parameter, value.DoubleValue);
                break;
            case StorageType.Integer:
                manager.Set(parameter, value.IntegerValue);
                break;
            case StorageType.String:
                manager.Set(parameter, value.TextValue ?? string.Empty);
                break;
            case StorageType.ElementId:
                manager.Set(
                    parameter,
                    SmartCon.Core.Compatibility.ElementIdCompat.Create(value.ElementIdValue));
                break;
        }
    }

    internal static bool RawEquals(FamilyValueSnapshot left, FamilyValueSnapshot right)
    {
        if (left.StorageType != right.StorageType)
            return false;

        return string.Equals(left.ToRaw(), right.ToRaw(), StringComparison.Ordinal);
    }
}