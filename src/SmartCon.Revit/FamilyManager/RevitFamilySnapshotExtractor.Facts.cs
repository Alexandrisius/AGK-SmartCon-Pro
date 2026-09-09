using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    /// <summary>
    /// Category-driven facts per <see cref="FamilyFactRuleSet"/> (ADR-055).
    /// Every matched rule produces exactly one <see cref="FamilyFact"/> —
    /// a read-but-absent parameter becomes the documented empty-string
    /// sentinel so the actualization task's detection clears and the UI
    /// hides the row. Read failures are swallowed into the sentinel as
    /// well: facts are cosmetic metadata and must never break a snapshot.
    /// </summary>
    private static List<FamilyFact> ExtractFacts(Document familyDoc, int? categoryId)
    {
        var result = new List<FamilyFact>();
        if (categoryId is null) return result;

        var rules = FamilyFactRuleSet.GetRulesForCategory(categoryId.Value);
        if (rules.Count == 0) return result;

        foreach (var rule in rules)
        {
            // A null fact = the computed value is unavailable this run —
            // OMIT it so the actualization detection stays pending and
            // self-heals (a sentinel would permanently clear the detection).
            var fact = ReadFact(familyDoc, rule);
            if (fact is not null)
            {
                result.Add(fact);
            }
        }
        return result;
    }

    private static FamilyFact ReadFact(Document familyDoc, FamilyFactRule rule)
    {
        // Computed facts (no backing parameter) have their own source —
        // the connector-shape mask comes from the family's ConnectorElements
        // (owner stress test 2026-09-01, баг 8: the routing picker filters
        // flex-duct candidates by connector profile).
        if (rule.ParameterId == FamilyFactRuleSet.ComputedFactParameterId)
        {
            return string.Equals(rule.FactKey, FamilyFactRuleSet.ConnectorShapeFactKey, StringComparison.Ordinal)
                ? ReadConnectorShapeFact(familyDoc, rule)
                : new FamilyFact(rule.FactKey, string.Empty, string.Empty);
        }

        // Sentinel: the fact was evaluated but the source parameter is
        // absent/unset in this family — detection clears, UI hides.
        var sentinel = new FamilyFact(rule.FactKey, string.Empty, string.Empty);

        try
        {
            var param = familyDoc.OwnerFamily?.get_Parameter((BuiltInParameter)rule.ParameterId);
            if (param is null || !param.HasValue)
            {
                SmartConLogger.Debug(
                    $"ExtractFacts: '{rule.FactKey}' parameter unavailable in '{familyDoc.Title}' — sentinel written");
                return sentinel;
            }

            switch (param.StorageType)
            {
                case StorageType.Integer:
                    var intVal = param.AsInteger();
                    // Part Type reads as a raw int; the enum member name is
                    // the stable human fallback (AsValueString would return
                    // the bare number for this parameter).
                    var display = rule.FactKey == FamilyFactRuleSet.PartTypeFactKey
                        ? ((PartType)intVal).ToString()
                        : intVal.ToString(CultureInfo.InvariantCulture);
                    return new FamilyFact(
                        rule.FactKey,
                        intVal.ToString(CultureInfo.InvariantCulture),
                        display);

                case StorageType.String:
                    var strVal = param.AsString();
                    return string.IsNullOrEmpty(strVal)
                        ? sentinel
                        : new FamilyFact(rule.FactKey, strVal, strVal);

                default:
                    return sentinel;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"ExtractFacts: read of '{rule.FactKey}' failed in '{familyDoc.Title}': {ex.Message}");
            return sentinel;
        }
    }

    /// <summary>
    /// Connector-shape bitmask of the family (Round=1, Rectangular=2,
    /// Oval=4 — a multi-shape transition like oval-round reports BOTH bits,
    /// so the picker matches it on either end). A family genuinely without
    /// connectors reports mask 0 (never matches a shape-filtered picker —
    /// correct: it cannot serve routing); a collector failure OMITS the
    /// fact entirely so the actualization detection stays pending and
    /// self-heals instead of permanently hiding the family from pickers.
    /// </summary>
    private static FamilyFact ReadConnectorShapeFact(Document familyDoc, FamilyFactRule rule)
    {
        var mask = 0;
        var names = new List<string>();
        try
        {
            var connectors = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(ConnectorElement))
                .Cast<ConnectorElement>();
            foreach (var connector in connectors)
            {
                switch (connector.Shape)
                {
                    case ConnectorProfileType.Round:
                        mask |= 1;
                        if (!names.Contains("Round")) names.Add("Round");
                        break;
                    case ConnectorProfileType.Rectangular:
                        mask |= 2;
                        if (!names.Contains("Rectangular")) names.Add("Rectangular");
                        break;
                    case ConnectorProfileType.Oval:
                        mask |= 4;
                        if (!names.Contains("Oval")) names.Add("Oval");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"ExtractFacts: connector-shape read failed in '{familyDoc.Title}': {ex.Message} — fact omitted (detection stays pending)");
            return null!;
        }

        return new FamilyFact(
            rule.FactKey,
            mask.ToString(CultureInfo.InvariantCulture),
            names.Count > 0 ? string.Join("+", names) : string.Empty);
    }

    /// <summary>
    /// Behavior flags from the <c>Family</c> element (ADR-056): shared,
    /// work-plane-based, always-vertical, cut-with-voids. These are
    /// built-in parameters on <c>OwnerFamily</c>, invisible to
    /// <c>FamilyManager.GetParameters()</c>.
    /// </summary>
    private static FamilyBehaviorFlags? ExtractBehaviorFlags(Document familyDoc)
    {
        var family = familyDoc.OwnerFamily;
        if (family is null) return null;

        return new FamilyBehaviorFlags(
            IsShared: ReadBoolFlag(family, BuiltInParameter.FAMILY_SHARED),
            IsWorkPlaneBased: ReadBoolFlag(family, BuiltInParameter.FAMILY_WORK_PLANE_BASED),
            IsAlwaysVertical: ReadBoolFlag(family, BuiltInParameter.FAMILY_ALWAYS_VERTICAL),
            AllowsCutWithVoids: ReadBoolFlag(family, BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS));
    }

    private static bool? ReadBoolFlag(Element element, BuiltInParameter builtInParameter)
    {
        try
        {
            var param = element.get_Parameter(builtInParameter);
            if (param is null || !param.HasValue || param.StorageType != StorageType.Integer)
                return null;
            return param.AsInteger() != 0;
        }
        catch
        {
            return null;
        }
    }
}
