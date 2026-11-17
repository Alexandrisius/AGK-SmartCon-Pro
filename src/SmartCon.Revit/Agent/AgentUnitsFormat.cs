using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent;

internal static class AgentUnitsFormat
{
    /// <summary>
    /// Converts internal feet to millimetres, rounded to 0.1 mm. Unrounded values
    /// leak floating-point noise into tool results (2299.9999999999995 instead of
    /// 2300), which models then try to "fix" in their answers (dogfood finding #2).
    /// </summary>
    internal static double ToMillimeters(double internalValue) =>
        Math.Round(
            UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters),
            1,
            MidpointRounding.AwayFromZero);

    internal static string GetLengthUnitsLabel(Document doc)
    {
        try
        {
            var unitTypeId = doc.GetUnits()
                .GetFormatOptions(SpecTypeId.Length)
                .GetUnitTypeId();
            return LabelUtils.GetLabelForUnit(unitTypeId);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}