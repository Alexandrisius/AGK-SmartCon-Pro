using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;

namespace SmartCon.FamilyManager.Services.Validation;

/// <summary>
/// Maps catalog <see cref="ExtractedAttributeValue"/> rows (active version,
/// already in SQLite) to <see cref="FamilyValidationInput"/> — the
/// category-change gate (DnD / properties dialog) revalidates from
/// persisted data without re-opening the managed .rfa.
/// <para>
/// Status semantics: <see cref="AttributeValueStatus.Found"/> → present
/// with value; <see cref="AttributeValueStatus.EmptyValue"/> → present,
/// no value; <see cref="AttributeValueStatus.ReadError"/> /
/// <see cref="AttributeValueStatus.UnsupportedStorageType"/> → present,
/// no provable value (hard gate treats unprovable as failing);
/// <see cref="AttributeValueStatus.MissingParameter"/> /
/// <see cref="AttributeValueStatus.NotInFamily"/> → absent.
/// </para>
/// Pure C# — safe to invoke from any thread.
/// </summary>
internal static class ExtractedValuesValidationMapper
{
    public static FamilyValidationInput ToValidationInput(
        IReadOnlyList<ExtractedAttributeValue> values,
        IReadOnlyDictionary<string, string> typeNames)
    {
        // Typed rows only: untyped (type_id IS NULL) rows are instance-scope
        // leftovers that would phantom-fail the family when grouped as a
        // synthetic "<default>" type alongside real types. They are used
        // only as a fallback for families whose extraction produced no
        // typed rows at all.
        var typed = values.Where(v => v.TypeId is not null).ToList();
        var source = typed.Count > 0 ? typed : values.ToList();

        var types = source
            .GroupBy(v => v.TypeId)
            .Select(g => new FamilyTypeValidationData(
                ResolveTypeName(g.Key, typeNames),
                g.Select(ToValidationValue).ToList()))
            .ToList();

        return new FamilyValidationInput(types);
    }

    private static string ResolveTypeName(string? typeId, IReadOnlyDictionary<string, string> typeNames)
    {
        if (typeId is not null && typeNames.TryGetValue(typeId, out var name))
        {
            return name;
        }

        return FamilyTypeSnapshot.DefaultTypeName;
    }

    private static ParameterValidationValue ToValidationValue(ExtractedAttributeValue v)
    {
        var (isPresent, hasProvableValue) = v.Status switch
        {
            AttributeValueStatus.Found => (true, true),
            AttributeValueStatus.EmptyValue => (true, false),
            AttributeValueStatus.ReadError => (true, false),
            AttributeValueStatus.UnsupportedStorageType => (true, false),
            _ => (false, false),
        };

        return new ParameterValidationValue(
            v.ParameterName,
            isPresent,
            ValueText: hasProvableValue ? v.ValueText : null,
            ValueNumber: hasProvableValue ? v.ValueNumber : null,
            UnitTypeId: v.UnitTypeId,
            // value_text of a catalog row carries the display string
            // ("300 мм") whenever extraction formatted one — parsing it
            // yields the display-unit number for numeric rules.
            DisplayNumber: hasProvableValue ? DisplayValueParser.TryParseNumber(v.ValueText) : null);
    }
}
