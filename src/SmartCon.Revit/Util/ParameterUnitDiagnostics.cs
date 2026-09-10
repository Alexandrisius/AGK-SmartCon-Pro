using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;

namespace SmartCon.Revit.Util;

/// <summary>
/// Diagnostics for the FamilyManager attribute-units investigation:
/// logs the owning document's unit format settings. Debug-level only —
/// silent in Release builds. Grep key: "UnitDiag".
/// (The per-parameter raw/AsValueString logging was removed after the
/// investigation concluded — it produced thousands of lines per
/// actualization run, see the #249 log audit.)
/// </summary>
public static class ParameterUnitDiagnostics
{
    public static void LogDocumentUnits(Document doc, string source)
    {
        try
        {
            var units = doc.GetUnits();
#if REVIT2021_OR_GREATER
            LogSpec(units, SpecTypeId.Length, "Length", source);
            LogSpec(units, SpecTypeId.PipeSize, "PipeSize", source);
            LogSpec(units, SpecTypeId.PipingPressure, "PipingPressure", source);
            LogSpec(units, SpecTypeId.HvacPressure, "HvacPressure", source);
            LogSpec(units, SpecTypeId.Number, "Number", source);
#else
            LogSpecLegacy(units, UnitType.UT_Length, "Length", source);
            LogSpecLegacy(units, UnitType.UT_Piping_Pressure, "PipingPressure", source);
            LogSpecLegacy(units, UnitType.UT_HVAC_Pressure, "HvacPressure", source);
            LogSpecLegacy(units, UnitType.UT_Number, "Number", source);
#endif
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"UnitDiag [{source}] units context failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

#if REVIT2021_OR_GREATER
    private static void LogSpec(Units units, ForgeTypeId spec, string label, string source)
    {
        try
        {
            var fo = units.GetFormatOptions(spec);
            var unitId = fo.GetUnitTypeId();
            var symbol = "<n/a>";
            if (unitId is not null && FormatOptions.CanHaveSymbol(unitId))
            {
                var symId = fo.GetSymbolTypeId();
                symbol = symId is null || symId.Empty() ? "<none>" : symId.TypeId;
            }

            SmartConLogger.Debug(
                $"UnitDiag [{source}] {label}: unit={(unitId?.TypeId ?? "<none>")}, symbol={symbol}, " +
                $"accuracy={fo.Accuracy.ToString(CultureInfo.InvariantCulture)}, useDefault={fo.UseDefault}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"UnitDiag [{source}] {label}: <{ex.GetType().Name}: {ex.Message}>");
        }
    }
#else
    private static void LogSpecLegacy(Units units, UnitType unitType, string label, string source)
    {
        try
        {
            var fo = units.GetFormatOptions(unitType);
            SmartConLogger.Debug(
                $"UnitDiag [{source}] {label}: displayUnits={fo.DisplayUnits}, " +
                $"accuracy={fo.Accuracy.ToString(CultureInfo.InvariantCulture)}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"UnitDiag [{source}] {label}: <{ex.GetType().Name}: {ex.Message}>");
        }
    }
#endif
}
