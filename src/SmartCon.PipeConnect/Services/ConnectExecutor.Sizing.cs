using Autodesk.Revit.DB;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

public sealed partial class ConnectExecutor
{
    /// <summary>
    /// Size a fitting's connectors to match static and dynamic radii.
    /// Handles pair-mode (type parameter) and sequential-mode sizing.
    /// Optionally adjusts the dynamic element radius to match the fitting.
    /// </summary>
    /// <param name="context">Operation context.</param>
    /// <param name="activeDynamic">Current dynamic connector state.</param>
    /// <param name="fittingId">Fitting element to size.</param>
    /// <param name="fitConn2">Expected secondary connector index, or null for auto-detection.</param>
    /// <param name="activeFittingRule">Mapping rule for CTC resolution.</param>
    /// <param name="adjustDynamicToFit">Whether to adjust the dynamic element to match the fitting.</param>
    /// <param name="upstreamTarget">Upstream connector for alignment (defaults to static).</param>
    /// <returns>Sizing result with updated connectors.</returns>
    public SizeFittingResult SizeFittingConnectors(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId fittingId,
        ConnectorProxy? fitConn2,
        FittingMappingRule? activeFittingRule,
        bool adjustDynamicToFit = true,
        ConnectorProxy? upstreamTarget = null)
    {
        using var _scope = SmartConLogger.BeginScope("SizeFitting",
            ("Method", "SizeFittingConnectors"),
            ("FittingId", fittingId.GetValue()),
            ("AdjustDynamicToFit", adjustDynamicToFit));

        ConnectorProxy? result = null;
        ConnectorProxy? currentDynamic = activeDynamic;

        try
        {
            var effectiveUpstream = upstreamTarget ?? context.Session.StaticConnector;
            var allConns = _connSvc.GetAllFreeConnectors(context.Doc, fittingId).ToList();
            if (allConns.Count == 0) return new SizeFittingResult(null, currentDynamic);

            ConnectorProxy? resolvedConn2;
            ConnectorProxy? resolvedConn1;
            if (fitConn2 is not null && allConns.Any(c => c.ConnectorIndex == fitConn2.ConnectorIndex))
            {
                resolvedConn2 = allConns.First(c => c.ConnectorIndex == fitConn2.ConnectorIndex);
                resolvedConn1 = allConns.FirstOrDefault(c => c.ConnectorIndex != fitConn2.ConnectorIndex);
            }
            else
            {
                var ordered = allConns
                    .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, effectiveUpstream.OriginVec3))
                    .ToList();
                resolvedConn1 = ordered.FirstOrDefault();
                resolvedConn2 = ordered.Skip(1).FirstOrDefault();
            }

            int conn1Idx = resolvedConn1?.ConnectorIndex ?? -1;
            int conn2Idx = resolvedConn2?.ConnectorIndex ?? -1;

            var depsByIdx = new Dictionary<int, IReadOnlyList<ParameterDependency>>();
            foreach (var c in allConns)
                depsByIdx[c.ConnectorIndex] = _paramResolver.GetConnectorRadiusDependencies(context.Doc, fittingId, c.ConnectorIndex);

            bool usePairMode = conn1Idx >= 0 && conn2Idx >= 0
                && depsByIdx.TryGetValue(conn1Idx, out var d1) && d1.Count > 0 && !d1[0].IsInstance
                && depsByIdx.TryGetValue(conn2Idx, out var d2) && d2.Count > 0 && !d2[0].IsInstance;

            if (usePairMode)
            {
                double currentDynRadius = currentDynamic?.Radius ?? context.Session.DynamicConnector.Radius;
                double achievedDynRadius = currentDynRadius;

                context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_FittingSize"), txDoc =>
                {
                    var (_, dynR) = _paramResolver.TrySetFittingTypeForPair(
                        txDoc, fittingId,
                        conn1Idx, effectiveUpstream.Radius,
                        conn2Idx, currentDynRadius);
                    achievedDynRadius = dynR;
                    txDoc.Regenerate();
                });

                const double eps = 1e-6;
                // Element-wise chain mode: the dynamic being fitted is the ACTIVE
                // connection point's dynamic, not necessarily the session root.
                var dynId = currentDynamic?.OwnerElementId ?? context.Session.DynamicConnector.OwnerElementId;
                var dynConnIdx = currentDynamic?.ConnectorIndex ?? context.Session.DynamicConnector.ConnectorIndex;
                double actualDynRadius = currentDynamic?.Radius ?? currentDynRadius;
                if (adjustDynamicToFit && System.Math.Abs(achievedDynRadius - actualDynRadius) > eps)
                {
                    SmartConLogger.Info($"Adjusting dynamic: {actualDynRadius * FeetToMm:F2}mm → {achievedDynRadius * FeetToMm:F2}mm");
                    context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_FitDynamicToFitting"), txDoc =>
                    {
                        _paramResolver.TrySetConnectorRadius(txDoc, dynId, dynConnIdx, achievedDynRadius);
                        txDoc.Regenerate();
                    });
                }
                else if (!adjustDynamicToFit)
                {
                    SmartConLogger.Info($"Skipping dynamic adjustment (adjustDynamicToFit=false). Actual dynamic={actualDynRadius * FeetToMm:F2}mm, fitting achieved={achievedDynRadius * FeetToMm:F2}mm");
                }
            }
            else
            {
                double currentDynRadius = currentDynamic?.Radius ?? context.Session.DynamicConnector.Radius;

                context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_FittingSize"), txDoc =>
                {
                    var sortedConns = allConns
                        .OrderBy(c =>
                        {
                            if (!depsByIdx.TryGetValue(c.ConnectorIndex, out var deps) || deps.Count == 0)
                                return 1;
                            return deps[0].IsInstance ? 1 : 0;
                        })
                        .ToList();

                    foreach (var c in sortedConns)
                    {
                        double targetRadius = c.ConnectorIndex == conn2Idx
                            ? currentDynRadius
                            : effectiveUpstream.Radius;
                        _paramResolver.TrySetConnectorRadius(txDoc, fittingId, c.ConnectorIndex, targetRadius);
                    }
                    txDoc.Regenerate();
                });
            }

            var (realignFitConn2, updatedDynamic) = RealignAfterSizing(
                context, currentDynamic, fittingId, activeFittingRule, effectiveUpstream);
            currentDynamic = updatedDynamic;
            result = realignFitConn2;
        }
        catch (Exception ex) { SmartConLogger.Warn($"Best-effort error in SizeFitting: {ex.Message} [Action: соединение продолжится без подбора размера — проверьте размеры фитинга вручную]"); }

        return new SizeFittingResult(result, currentDynamic);
    }

    private (ConnectorProxy? FitConn2, ConnectorProxy? ActiveDynamic) RealignAfterSizing(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId fittingId,
        FittingMappingRule? activeFittingRule,
        ConnectorProxy upstreamTarget)
    {
        using var _scope = SmartConLogger.BeginScope("RealignAfterSizing",
            ("Method", "RealignAfterSizing"),
            ("FittingId", fittingId.GetValue()));

        ConnectorProxy? newFitConn2 = null;
        ConnectorProxy? currentDynamic = activeDynamic;

        try
        {
            context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_AlignAfterSize"), txDoc =>
            {
                var ctcOvr = context.VirtualCtcStore.GetOverridesForElement(fittingId);
                // Effective CTC dynamic: повторный align после sizing обязан использовать тот
                // же dynCtc, что и align при вставке — иначе при dynCtc=0 срабатывает Strategy 1
                // (прямая) вместо Strategy 0 (cross) и reducer переворачивается обратно (кейс #167).
                var dynCtc = currentDynamic?.ConnectionTypeCode.IsDefined == true
                    ? currentDynamic.ConnectionTypeCode
                    : _ctcManager.ResolveDynamicTypeFromRule(activeFittingRule, upstreamTarget.ConnectionTypeCode);

                newFitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                    txDoc, fittingId, upstreamTarget, _transformSvc, _connSvc,
                    dynamicTypeCode: dynCtc,
                    ctcOverrides: ctcOvr.Count > 0 ? ctcOvr : null,
                    directConnectRules: _mappingRepo.GetMappingRules());

                if (newFitConn2 is not null && currentDynamic is not null)
                {
                    var dynProxy = _connSvc.RefreshConnector(
                        txDoc, currentDynamic.OwnerElementId, currentDynamic.ConnectorIndex)
                        ?? currentDynamic;

                    var offset = newFitConn2.OriginVec3 - dynProxy.OriginVec3;
                    if (!VectorUtils.IsZero(offset))
                        PipeAbsorptionApplier.MoveOrAbsorb(
                            txDoc, _transformSvc, currentDynamic.OwnerElementId, dynProxy.OriginVec3, offset);

                    txDoc.Regenerate();

                    currentDynamic = _ctcManager.RefreshWithCtcOverride(
                        txDoc, currentDynamic.OwnerElementId, currentDynamic.ConnectorIndex)
                        ?? currentDynamic;
                }

                txDoc.Regenerate();
            });
        }
        catch (Exception ex) { SmartConLogger.Warn($"Best-effort error in RealignAfterSizing: {ex.Message} [Action: соединение продолжится — проверьте выравнивание коннекторов вручную]"); }

        return (newFitConn2, currentDynamic);
    }
}
