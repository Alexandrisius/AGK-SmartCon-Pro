using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent;

internal static class AgentUnitsFormat
{
    internal static double ToMillimeters(double internalValue) =>
        UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters);

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