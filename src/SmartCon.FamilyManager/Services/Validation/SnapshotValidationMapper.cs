using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;

namespace SmartCon.FamilyManager.Services.Validation;

/// <summary>
/// Maps in-memory <see cref="FamilySnapshot"/> / <see cref="SystemFamilySnapshot"/>
/// (produced in Phase 1 Prepare, document still open) to
/// <see cref="FamilyValidationInput"/> for <c>IFamilyValidationEngine</c> —
/// the import-time rule check runs against the same single-open snapshot,
/// no .rfa re-open. Pure C# — safe to invoke from any thread.
/// <para>
/// <see cref="ParameterValidationValue.DisplayNumber"/> is parsed from
/// the snapshot's <c>ValueDisplay</c> ("300 мм" → 300) so numeric rules
/// authored in display units compare against display values; the raw
/// internal-unit <see cref="ParameterValidationValue.ValueNumber"/> stays
/// as the engine's fallback.
/// </para>
/// </summary>
internal static class SnapshotValidationMapper
{
    public static FamilyValidationInput ToValidationInput(FamilySnapshot snapshot)
    {
        var types = snapshot.Types
            .Select(t => new FamilyTypeValidationData(
                t.Name,
                t.Values.Select(ToValidationValue).ToList()))
            .ToList();

        return new FamilyValidationInput(types);
    }

    public static FamilyValidationInput ToValidationInput(SystemFamilySnapshot snapshot)
    {
        var types = snapshot.Types
            .Select(t => new FamilyTypeValidationData(
                t.Name,
                t.Values.Select(ToValidationValue).ToList()))
            .ToList();

        return new FamilyValidationInput(types);
    }

    private static ParameterValidationValue ToValidationValue(FamilyParameterValue v) =>
        new(
            v.ParameterName,
            IsPresent: true,
            ValueText: v.HasValue ? v.ValueText : null,
            ValueNumber: v.HasValue ? v.ValueNumber : null,
            UnitTypeId: v.UnitTypeId,
            DisplayNumber: v.HasValue ? DisplayValueParser.TryParseNumber(v.ValueDisplay) : null);

    private static ParameterValidationValue ToValidationValue(SystemParameterValue v) =>
        new(
            v.ParameterName,
            IsPresent: true,
            ValueText: v.HasValue ? v.ValueText : null,
            ValueNumber: v.HasValue ? v.ValueNumber : null,
            UnitTypeId: v.UnitTypeId,
            DisplayNumber: v.HasValue ? DisplayValueParser.TryParseNumber(v.ValueDisplay) : null);
}
