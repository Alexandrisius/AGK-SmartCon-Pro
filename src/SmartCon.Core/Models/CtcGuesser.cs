namespace SmartCon.Core.Models;

/// <summary>
/// Auto-guessing algorithms for CTC (ConnectionTypeCode) of fittings and reducers.
/// Pure logic without Revit API — tested via unit tests.
/// </summary>
public static class CtcGuesser
{
    /// <summary>
    /// Check whether two CTCs can connect directly via IsDirectConnect rules.
    /// </summary>
    public static bool CanDirectConnect(
        ConnectionTypeCode left, ConnectionTypeCode right,
        IReadOnlyList<FittingMappingRule> rules)
    {
        if (!left.IsDefined || !right.IsDefined) return false;

        foreach (var r in rules)
        {
            if (!r.IsDirectConnect) continue;
            if (r.FromType.Value == left.Value && r.ToType.Value == right.Value) return true;
            if (r.ToType.Value == left.Value && r.FromType.Value == right.Value) return true;
        }

        return false;
    }

    /// <summary>
    /// Find the CTC that has a direct connection (IsDirectConnect=true) with the given CTC.
    /// If multiple found — returns the first. If none found — Undefined.
    /// </summary>
    public static ConnectionTypeCode FindDirectConnectCounterpart(
        ConnectionTypeCode ctc, IReadOnlyList<FittingMappingRule> rules)
    {
        if (!ctc.IsDefined) return ConnectionTypeCode.Undefined;

        foreach (var r in rules)
        {
            if (!r.IsDirectConnect) continue;
            if (r.FromType.Value == ctc.Value) return r.ToType;
            if (r.ToType.Value == ctc.Value) return r.FromType;
        }
        return ConnectionTypeCode.Undefined;
    }

    /// <summary>
    /// Auto-guess a CTC pair for an adapter/fitting.
    /// Returns (counterpartForStatic, counterpartForDynamic).
    /// </summary>
    public static (ConnectionTypeCode ForStatic, ConnectionTypeCode ForDynamic) GuessAdapterCtc(
        ConnectionTypeCode staticCTC, ConnectionTypeCode dynamicCTC,
        IReadOnlyList<FittingMappingRule> rules)
    {
        // Find counterpart for each side via IsDirectConnect rules.
        // Thread↔Thread -> counterpart(Thread)=NP -> fitting: NP-NP (nipple)
        // Weld↔Weld -> counterpart(Weld)=Weld -> fitting: Weld-Weld
        // Thread↔Weld -> counterpart(Thread)=NP, counterpart(Weld)=Weld -> fitting: NP-Weld
        var forStatic = FindDirectConnectCounterpart(staticCTC, rules);
        var forDynamic = FindDirectConnectCounterpart(dynamicCTC, rules);

        // Fallback: if counterpart not found — use the CTC itself
        if (!forStatic.IsDefined) forStatic = staticCTC;
        if (!forDynamic.IsDefined) forDynamic = dynamicCTC;

        return (forStatic, forDynamic);
    }

    /// <summary>
    /// Auto-guess a CTC pair for a reducer.
    /// Same-type -> both = same CTC.
    /// Cross-type -> reverse (conn to static = dynamicCTC, conn to dynamic = staticCTC).
    /// </summary>
    public static (ConnectionTypeCode ForStaticSide, ConnectionTypeCode ForDynamicSide) GuessReducerCtc(
        ConnectionTypeCode staticCTC, ConnectionTypeCode dynamicCTC,
        IReadOnlyList<FittingMappingRule> rules)
    {
        var forStatic = FindDirectConnectCounterpart(staticCTC, rules);
        var forDynamic = FindDirectConnectCounterpart(dynamicCTC, rules);

        if (forStatic.IsDefined && forDynamic.IsDefined)
            return (forStatic, forDynamic);

        if (forStatic.IsDefined)
        {
            forDynamic = staticCTC;
            return (forStatic, forDynamic);
        }

        if (forDynamic.IsDefined)
        {
            forStatic = dynamicCTC;
            return (forStatic, forDynamic);
        }

        return (dynamicCTC, staticCTC);
    }
}
