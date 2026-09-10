using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.PipeConnect.Services;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// "Блокировать" (LockNetwork) instant toggle (issues #165, #167).
/// The toggle always governs the LAST COMPLETED connection — never the whole
/// attached network: at depth 0 that is the root dynamic, at depth N it is
/// queue[N] (re-attach only it via one detach + one attach in the new mode).
///
/// Root switching is unified for every element type via the snapshot mechanism:
/// ON restores the Init baseline (undo absorb, resize and ChangeTypeId), rigidly
/// re-aligns to static, and inserts a reducer when the baseline DN mismatches the
/// static DN — the network DN is never mutated. OFF removes the toggle-inserted
/// reducer and restores the compensated state (absorb / autosized DN).
/// Future "+" then runs in the toggle's mode (ON: rigid as-is + reducer on
/// mismatch, no seal; OFF: absorb + early seal). Default behavior (toggle off at
/// session start) is unchanged.
/// </summary>
public sealed partial class PipeConnectEditorViewModel
{
    /// <summary>Position tolerance for "root still at static" checks (~0.03 mm).</summary>
    private const double RootPositionCheckEpsFt = 1e-4;

    /// <summary>Radius tolerance for DN mismatch checks (same as ConnectExecutor).</summary>
    private const double RootRadiusCheckEps = 1e-5;

    partial void OnLockNetworkChanged(bool value)
    {
        if (!IsSessionActive || IsBusy || _groupSession is null) return;

        if (value)
            ApplyLockNetworkMode();
        else
            ApplyAbsorbNetworkMode();
    }

    /// <summary>
    /// Toggle ON: switch the LAST COMPLETED connection to rigid mode. Depth N:
    /// re-attach only queue[N]. Depth 0: restore the root baseline snapshot
    /// (undoes absorb/DN-change), rigidly align to static, insert a reducer on
    /// DN mismatch. The rest of the attached network stays untouched.
    /// </summary>
    private void ApplyLockNetworkMode()
    {
        using var _scope = SmartConLogger.BeginScope("EditorLock",
            ("Method", "ApplyLockNetworkMode"));

        try
        {
            if (ChainDepth > 0)
            {
                SmartConLogger.Info($"LockNetwork ON: re-attaching last chain element (depth={ChainDepth}) " +
                    "in rigid mode — rest of network preserved");

                DecrementChainDepth();
                if (TryIncrementChainDepth(out _))
                    StatusMessage = LocalizationService.GetString("Status_LockNetworkAppliedTail");
                return;
            }

            UnsealIfSealed("Блокировать");

            // Точка подключения root: static для Direct-топологии, fitting conn2 —
            // для FittingOnly (root физически стоит у фитинга, не у static — иначе
            // проверка «root не тронут» всегда падает в soft mode и baseline restore
            // с триггером reducer никогда не срабатывает для соединения типа
            // «ниппель», #167).
            var upstream = ResolveLockUpstream();

            if (_rootBaselineSnapshot is null || !IsRootAtUpstream(upstream, out var rootDyn))
            {
                // Soft mode: the root state is left untouched — so there is nothing
                // to restore on OFF either; drop any stale compensated snapshot.
                _rootCompensatedSnapshot = null;
                SmartConLogger.Info("LockNetwork ON: root moved since Init — soft mode (unseal + rigid for future attaches)");
                UpdateChainUI();
                StatusMessage = LocalizationService.GetString("Status_LockNetworkApplied");
                return;
            }

            _rootCompensatedSnapshot = _chainOpHandler.CaptureSnapshot(
                _doc, rootDyn.OwnerElementId, _chainGraph);

            double baselineDnMm = 0, upstreamDnMm = 0;

            _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_LockNetworkRigid"), doc =>
            {
                _chainOpHandler.RestoreElementFromSnapshot(doc, rootDyn.OwnerElementId, _rootBaselineSnapshot);
                doc.Regenerate();

                var fresh = _connSvc.RefreshConnector(doc, rootDyn.OwnerElementId, rootDyn.ConnectorIndex);
                if (fresh is not null)
                {
                    var align = ConnectorAligner.ComputeAlignment(
                        upstream.OriginVec3, upstream.BasisZVec3, upstream.BasisXVec3,
                        fresh.OriginVec3, fresh.BasisZVec3, fresh.BasisXVec3);
                    _alignmentSvc.ApplyAlignment(doc, rootDyn.OwnerElementId, align, fresh.ConnectorIndex);
                }
            });

            RefreshRootDynamicAfterLockToggle();

            // Fitting подбирается после baseline restore: Lock вернул динамику исходный DN,
            // и fitting обязан переподобраться под новую пару (static, baseline dyn) —
            // иначе его conn2 остаётся под pre-lock размер и mismatch ложно триггернет
            // reducer (кейс #167: ChangeDynamicSize DN20 → Lock DN32 → fitting застрял на DN20).
            if (_currentFittingId is not null)
            {
                SmartConLogger.Info("LockNetwork ON: re-sizing fitting to baseline dynamic DN");
                _activeFittingConn2 = SizeFittingConnectors(
                    _doc, _currentFittingId, _activeFittingConn2, adjustDynamicToFit: false)
                    ?? _activeFittingConn2;
                upstream = ResolveLockUpstream();
            }

            var rootAfter = _activeDynamic;
            bool reducerNotFound = false;
            if (rootAfter is not null)
            {
                baselineDnMm = rootAfter.Radius * 2.0 * FeetToMm;
                upstreamDnMm = upstream.Radius * 2.0 * FeetToMm;
            }

            SmartConLogger.Info($"LockNetwork ON: root baseline restored " +
                $"(DN {baselineDnMm:F0} vs upstream DN {upstreamDnMm:F0}), rigid aligned");

            if (rootAfter is not null
                && System.Math.Abs(rootAfter.Radius - upstream.Radius) > RootRadiusCheckEps)
            {
                SmartConLogger.Info("LockNetwork ON: DN mismatch after baseline restore — inserting reducer");

                // Guard against an orphan reducer: a pre-existing primary reducer of
                // this point is removed before inserting the new one.
                if (_primaryReducerId is not null)
                {
                    _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_CleanupOldReducer"), doc =>
                    {
                        _fittingInsertSvc.DeleteElement(doc, _primaryReducerId);
                        _virtualCtcStore.RemoveForElement(_primaryReducerId);
                    });
                    _primaryReducerId = null;
                    _needsPrimaryReducer = false;
                    _lockInsertedReducer = false;
                }

                if (_currentFittingId is not null && _activeFittingConn2 is not null)
                    InsertReducerBetweenFittingAndDynamic();
                else
                    InsertReducerBetweenStaticAndDynamic();
                _lockInsertedReducer = _primaryReducerId is not null;

                if (_lockInsertedReducer)
                {
                    _needsPrimaryReducer = true;
                    IsReducerVisible = true;
                    // AvailableReducers строится один раз из ChainPlan (Direct — без
                    // reducer'ов): перестроить из маппинга, иначе ComboBox виден пустым.
                    EnsureReducersForFittingPair(upstream, rootAfter);
                    MoveRootToReducerConn2(rootDyn, upstream);
                    RefreshRootDynamicAfterLockToggle();
                }
                else
                {
                    reducerNotFound = true;
                }
            }

            UpdateChainUI();
            StatusMessage = LocalizationService.GetString(
                reducerNotFound ? "Status_LockReducerNotFound" : "Status_LockNetworkApplied");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
    }

    /// <summary>
    /// Toggle OFF: switch the LAST COMPLETED connection back to compensation mode.
    /// Depth N: re-attach only queue[N] in absorb mode. Depth 0: remove the
    /// toggle-inserted reducer, restore the compensated snapshot (absorb/autosized
    /// DN), re-attempt the seal.
    /// </summary>
    private void ApplyAbsorbNetworkMode()
    {
        using var _scope = SmartConLogger.BeginScope("EditorLock",
            ("Method", "ApplyAbsorbNetworkMode"));

        try
        {
            if (ChainDepth > 0)
            {
                SmartConLogger.Info($"LockNetwork OFF: re-attaching last chain element (depth={ChainDepth}) " +
                    "in compensation mode — rest of network preserved");

                DecrementChainDepth();
                if (TryIncrementChainDepth(out _))
                    StatusMessage = LocalizationService.GetString("Status_LockNetworkRevertedTail");
                return;
            }

            UnsealIfSealed("снятие Блокировать");

            if (_lockInsertedReducer && _primaryReducerId is not null)
            {
                _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_CleanupOldReducer"), doc =>
                {
                    _fittingInsertSvc.DeleteElement(doc, _primaryReducerId);
                    _virtualCtcStore.RemoveForElement(_primaryReducerId);
                });
                _primaryReducerId = null;
                _needsPrimaryReducer = false;
                IsReducerVisible = false;
                _lockInsertedReducer = false;
                SmartConLogger.Info("LockNetwork OFF: toggle-inserted reducer removed");
            }

            if (_rootCompensatedSnapshot is not null)
            {
                var rootDyn = _rootDynamicConnector ?? _ctx.DynamicConnector;

                SmartConLogger.Info("LockNetwork OFF: restoring compensated state — any manual root edits since lock-on are discarded");

                _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_LockNetworkAbsorb"), doc =>
                {
                    _chainOpHandler.RestoreElementFromSnapshot(doc, rootDyn.OwnerElementId, _rootCompensatedSnapshot);
                    doc.Regenerate();
                });

                SmartConLogger.Info("LockNetwork OFF: compensated state restored (absorb/autosized DN)");

                // Fitting переподбирается обратно под compensated (pre-lock) DN динамика —
                // симметрично переподбору при Lock ON.
                if (_currentFittingId is not null)
                {
                    _activeFittingConn2 = SizeFittingConnectors(
                        _doc, _currentFittingId, _activeFittingConn2, adjustDynamicToFit: false)
                        ?? _activeFittingConn2;
                }
            }

            TrySealAtCurrentBoundary();
            RefreshRootDynamicAfterLockToggle();
            UpdateChainUI();
            StatusMessage = LocalizationService.GetString("Status_LockNetworkReverted");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
    }

    /// <summary>
    /// Upstream-коннектор точки подключения root: fitting conn2 при наличии фитинга,
    /// иначе session static (Direct-топология).
    /// </summary>
    private ConnectorProxy ResolveLockUpstream()
    {
        if (_currentFittingId is not null && _activeFittingConn2 is not null)
        {
            // RefreshWithCtcOverride: virtual CTC фитинга (Reflect/мини-селектор) учитывается —
            // иначе EnsureReducersForFittingPair получает CTC=0 и список переходов пуст.
            var fresh = _ctcManager.RefreshWithCtcOverride(
                _doc, _activeFittingConn2.OwnerElementId, _activeFittingConn2.ConnectorIndex);
            if (fresh is not null)
                return fresh;
        }
        return _ctx.StaticConnector;
    }

    /// <summary>
    /// Baseline restore applies only while the root is untouched since Init: the
    /// root connector is the session/current one and still sits at its upstream point
    /// (static for Direct, fitting conn2 for FittingOnly).
    /// </summary>
    private bool IsRootAtUpstream(ConnectorProxy upstream, out ConnectorProxy rootDyn)
    {
        rootDyn = _rootDynamicConnector ?? _ctx.DynamicConnector;

        var rootConn = _connSvc.RefreshConnector(_doc, rootDyn.OwnerElementId, rootDyn.ConnectorIndex);
        return rootConn is not null
            && VectorUtils.DistanceTo(rootConn.OriginVec3, upstream.OriginVec3) < RootPositionCheckEpsFt;
    }

    /// <summary>
    /// Shift the root (rigid — never absorb in lock mode) from the upstream point to
    /// the reducer's far connector, so the chain becomes upstream ↔ reducer ↔ root.
    /// </summary>
    private void MoveRootToReducerConn2(ConnectorProxy rootDyn, ConnectorProxy upstream)
    {
        if (_primaryReducerId is null) return;

        bool moved = false;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_LockNetworkRigid"), doc =>
        {
            var rConns = _connSvc.GetAllFreeConnectors(doc, _primaryReducerId);
            if (rConns.Count < 2)
            {
                SmartConLogger.Debug($"MoveRootToReducerConn2: only {rConns.Count} free connector(s) on reducer — skip");
                return;
            }

            var rConn1 = rConns
                .OrderBy(rc => VectorUtils.DistanceTo(rc.OriginVec3, upstream.OriginVec3))
                .First();
            var rConn2 = rConns.FirstOrDefault(rc => rc.ConnectorIndex != rConn1.ConnectorIndex);
            if (rConn2 is null)
            {
                SmartConLogger.Debug("MoveRootToReducerConn2: conn2 not resolved — skip");
                return;
            }

            var rootFresh = _connSvc.RefreshConnector(doc, rootDyn.OwnerElementId, rootDyn.ConnectorIndex);
            if (rootFresh is null)
            {
                SmartConLogger.Debug("MoveRootToReducerConn2: root refresh failed — skip");
                return;
            }

            var offset = rConn2.OriginVec3 - rootFresh.OriginVec3;
            if (!VectorUtils.IsZero(offset))
            {
                _transformSvc.MoveElement(doc, rootDyn.OwnerElementId, offset);
                moved = true;
            }
            else
            {
                SmartConLogger.Debug("MoveRootToReducerConn2: root already at conn2 (offset=0) — skip");
            }
            doc.Regenerate();
        });

        if (moved)
            SmartConLogger.Info($"LockNetwork ON: root shifted to reducer conn2 (id={_primaryReducerId.GetValue()})");
    }

    private void RefreshRootDynamicAfterLockToggle()
    {
        var rootDyn = _rootDynamicConnector ?? _ctx.DynamicConnector;
        _activeDynamic = _ctcManager.RefreshWithCtcOverride(
            _doc, rootDyn.OwnerElementId, rootDyn.ConnectorIndex) ?? _activeDynamic;
    }
}
