using Autodesk.Revit.DB;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

public sealed record SizeChangeResult(
    ConnectorProxy? ActiveDynamic,
    bool UserManuallyChangedSize,
    string? SizeChangeInfo,
    bool NeedsPrimaryReducer);

public sealed class PipeConnectSizeHandler(
    IConnectorService connSvc,
    ITransformService transformSvc,
    IParameterResolver paramResolver,
    FittingCtcManager ctcManager)
{
    public SizeChangeResult ChangeSize(
        Document doc,
        ITransactionGroupSession groupSession,
        PipeConnectSessionContext ctx,
        FamilySizeOption selectedOption,
        ConnectorProxy activeDynamic,
        ElementId? fittingId,
        ElementId? reducerId)
    {
        ConnectorProxy? updatedDynamic = activeDynamic;
        bool needsPrimaryReducer = false;

        var dynId = ctx.DynamicConnector.OwnerElementId;
        var dynIdx = ctx.DynamicConnector.ConnectorIndex;

        groupSession.RunInTransaction("PipeConnect — Смена размера dynamic", d =>
        {
            bool appliedViaQueryParams = ApplyQueryParamsIfExists(d, dynId, selectedOption);

            if (!appliedViaQueryParams)
            {
                foreach (var kvp in selectedOption.AllConnectorRadii)
                {
                    var connIdx = kvp.Key;
                    var targetRadius = kvp.Value;
                    bool success = paramResolver.TrySetConnectorRadius(d, dynId, connIdx, targetRadius);
                    SmartConLogger.Info($"[ChangeDynamicSize] TrySetConnectorRadius(connIdx={connIdx}, " +
                        $"targetDN={FamilySizeFormatter.ToDn(targetRadius)}): {(success ? "OK" : "FAILED")}");
                }
            }

            d.Regenerate();

            var refreshed = connSvc.RefreshConnector(d, dynId, dynIdx);
            if (refreshed is not null)
            {
                var correction = ctx.StaticConnector.OriginVec3 - refreshed.OriginVec3;
                if (!VectorUtils.IsZero(correction))
                {
                    var distMm = VectorUtils.Length(correction) * FeetToMm;
                    SmartConLogger.Info($"[ChangeDynamicSize] PositionCorrection: {distMm:F3} mm");
                    transformSvc.MoveElement(d, dynId, correction);
                }
            }

            d.Regenerate();

            var allConnsAfter = connSvc.GetAllConnectors(d, dynId);
            foreach (var c in allConnsAfter)
            {
                var actualDn = FamilySizeFormatter.ToDn(c.Radius);
                SmartConLogger.Info($"[ChangeDynamicSize] After change: conn[{c.ConnectorIndex}] = DN {actualDn}");
            }

            updatedDynamic = ctcManager.RefreshWithCtcOverride(d, dynId, dynIdx) ?? updatedDynamic;
            if (updatedDynamic is not null)
            {
                var actualDn = (int)Math.Round(updatedDynamic.Radius * 2.0 * FeetToMm);
                SmartConLogger.Info($"[ChangeDynamicSize] Target connector after change: DN {actualDn}");
            }
        });

        needsPrimaryReducer = DetectReducerNeeded(updatedDynamic, ctx, fittingId, reducerId);

        var sizeChangeInfo = BuildSizeChangeInfo(selectedOption);

        return new SizeChangeResult(
            updatedDynamic,
            UserManuallyChangedSize: true,
            sizeChangeInfo,
            needsPrimaryReducer);
    }

    internal static bool DetectReducerNeeded(
        ConnectorProxy? activeDynamic,
        PipeConnectSessionContext ctx,
        ElementId? fittingId,
        ElementId? reducerId)
    {
        if (activeDynamic is null) return false;
        if (fittingId is not null || reducerId is not null) return false;

        const double radiusEps = 1e-5;
        var dynRadius = activeDynamic.Radius;
        var staticRadius = ctx.StaticConnector.Radius;
        if (Math.Abs(dynRadius - staticRadius) > radiusEps)
        {
            SmartConLogger.Info($"[ChangeDynamicSize] Radii mismatch: dyn={dynRadius * FeetToMm:F1}mm, " +
                $"static={staticRadius * FeetToMm:F1}mm → reducer needed");
            return true;
        }
        return false;
    }

    internal static string? BuildSizeChangeInfo(FamilySizeOption selected)
    {
        if (selected.SymbolName is not null && selected.CurrentSymbolName is not null
            && selected.SymbolName != selected.CurrentSymbolName)
        {
            return $"Типоразмер: {selected.CurrentSymbolName} → {selected.SymbolName}";
        }
        return null;
    }

    public static FamilySizeOption? FindBestOptionForRadius(
        IConnectorService connSvc,
        Document doc,
        PipeConnectSessionContext ctx,
        IReadOnlyList<FamilySizeOption> availableSizes,
        double targetRadius,
        int dynIdx)
    {
        var nonAutoSizes = availableSizes.Where(s => !s.IsAutoSelect).ToList();
        if (nonAutoSizes.Count == 0) return null;

        var currentConns = connSvc.GetAllConnectors(doc, ctx.DynamicConnector.OwnerElementId);
        var currentRadii = new Dictionary<int, double>();
        foreach (var c in currentConns)
            currentRadii[c.ConnectorIndex] = c.Radius;

        return BestSizeMatcher.FindClosestByRadius(nonAutoSizes, targetRadius, dynIdx, currentRadii);
    }

    public static bool ApplyQueryParamsIfExists(Document doc, ElementId elementId, FamilySizeOption option)
    {
        if (option.QueryParamNames.Count == 0 || option.QueryParamRawValuesMm.Count == 0)
            return false;

        var element = doc.GetElement(elementId);
        if (element is null) return false;

        var fi = element as FamilyInstance;
        var symbol = fi?.Symbol;

        int setCount = 0;
        for (int i = 0; i < option.QueryParamNames.Count; i++)
        {
            var paramName = option.QueryParamNames[i];
            double rawMm = option.QueryParamRawValuesMm[i];

            var param = FindWritableParam(element, symbol, paramName);
            if (param is null)
            {
                SmartConLogger.Info($"[ApplyQueryParams] SKIP '{paramName}': not found on element or symbol");
                continue;
            }

            if (param.IsReadOnly)
            {
                SmartConLogger.Info($"[ApplyQueryParams] SKIP '{paramName}': ReadOnly (elem={element.Id.Value})");
                continue;
            }

            double valueFt = rawMm * MmToFeet;
            param.Set(valueFt);
            setCount++;
            SmartConLogger.Info($"[ApplyQueryParams] Set '{paramName}' = {rawMm:F2} mm ({valueFt:F6} ft) via {(symbol != null && symbol.LookupParameter(paramName) is not null ? "Symbol" : "Instance")}");
        }

        SmartConLogger.Info($"[ApplyQueryParams] Set {setCount}/{option.QueryParamNames.Count} query params for '{option.DisplayName}'");
        return setCount > 0;
    }

    public static Parameter? FindWritableParam(Element element, FamilySymbol? symbol, string paramName)
    {
        var param = element.LookupParameter(paramName);
        if (param is not null) return param;

        if (symbol is not null)
        {
            param = symbol.LookupParameter(paramName);
            if (param is not null) return param;
        }

        foreach (Parameter p in element.Parameters)
        {
            if (p.Definition is not null && string.Equals(p.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase))
                return p;
        }

        if (symbol is not null)
        {
            foreach (Parameter p in symbol.Parameters)
            {
                if (p.Definition is not null && string.Equals(p.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }

        return null;
    }
}
