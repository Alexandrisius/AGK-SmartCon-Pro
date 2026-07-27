using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.PipeConnect.Services;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// "Блокировать" (LockNetwork) instant toggle for pipe root dynamics (issue #165).
/// By default a pipe dynamic's initial alignment is absorbed into its geometry
/// (ADR-052) and the detached network is sealed back automatically. Switching the
/// toggle ON reverts the absorb, applies the rigid move instead, and tears down the
/// seal so the user attaches the network element-by-element in rigid as-is mode
/// (AttachSingleElement with lockNetwork=true: no absorb, no resize). Switching OFF
/// restores the absorb and re-attempts the seal. Default behavior (toggle off at
/// session start) is unchanged.
/// </summary>
public sealed partial class PipeConnectEditorViewModel
{
    /// <summary>Position tolerance for "root still at static" checks (~0.03 mm).</summary>
    private const double RootPositionCheckEpsFt = 1e-4;

    partial void OnLockNetworkChanged(bool value)
    {
        if (!IsSessionActive || IsBusy || _groupSession is null) return;

        if (value)
            ApplyLockNetworkMode();
        else
            ApplyAbsorbNetworkMode();
    }

    /// <summary>
    /// Toggle ON: revert the root absorb (rigid move), tear down the seal.
    /// After this the network stays detached on the spot — the user attaches it
    /// element-by-element (rigid as-is) or via ConnectAll (rigid fast path).
    /// </summary>
    private void ApplyLockNetworkMode()
    {
        using var _scope = SmartConLogger.BeginScope("EditorLock",
            ("Method", "ApplyLockNetworkMode"));

        try
        {
            RollbackChainLevels();
            UnsealIfSealed("Блокировать");

            if (CanUndoRootAbsorb())
            {
                var dynId = _ctx.DynamicConnector.OwnerElementId;

                _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_LockNetworkRigid"), doc =>
                {
                    bool reverted = _rootFlexPoints is not null
                        ? PipeAbsorptionApplier.RevertFlexPipe(doc, dynId, _rootFlexPoints)
                        : _rootPipeStart is not null && _rootPipeEnd is not null
                            && PipeAbsorptionApplier.RevertStraightPipe(doc, dynId, _rootPipeStart, _rootPipeEnd);

                    if (!reverted)
                    {
                        SmartConLogger.Warn("Root absorb revert failed — root left as-is " +
                            "[Action: проверьте трубу в модели и переключите «Блокировать» ещё раз]");
                        return;
                    }

                    _transformSvc.MoveElement(doc, dynId, _rootRigidOffset);
                    doc.Regenerate();
                });

                _rootRigidApplied = true;
                SmartConLogger.Info($"LockNetwork ON: root absorb reverted, " +
                    $"rigid offset={VectorUtils.Length(_rootRigidOffset) * FeetToMm:F1}mm, seal torn down");
            }
            else
            {
                SmartConLogger.Info("LockNetwork ON: root already rigid (no absorb at Init or root moved since) — seal torn down only");
            }

            RefreshRootDynamicAfterLockToggle();
            UpdateChainUI();
            StatusMessage = LocalizationService.GetString("Status_LockNetworkApplied");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
    }

    /// <summary>
    /// Toggle OFF: undo the rigid move, restore the absorb (same geometry as after
    /// Init), and re-attempt the seal. Default ADR-052 behavior resumes.
    /// </summary>
    private void ApplyAbsorbNetworkMode()
    {
        using var _scope = SmartConLogger.BeginScope("EditorLock",
            ("Method", "ApplyAbsorbNetworkMode"));

        try
        {
            RollbackChainLevels();
            UnsealIfSealed("снятие Блокировать");

            if (_rootRigidApplied)
            {
                var rootDyn = _rootDynamicConnector ?? _ctx.DynamicConnector;
                var rootConn = _connSvc.RefreshConnector(_doc, rootDyn.OwnerElementId, rootDyn.ConnectorIndex);
                bool atStatic = rootConn is not null
                    && VectorUtils.DistanceTo(rootConn.OriginVec3, _ctx.StaticConnector.OriginVec3) < RootPositionCheckEpsFt;

                if (atStatic)
                {
                    var dynId = rootDyn.OwnerElementId;

                    _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_LockNetworkAbsorb"), doc =>
                    {
                        _transformSvc.MoveElement(doc, dynId, -_rootRigidOffset);
                        doc.Regenerate();

                        var afterRevert = _connSvc.RefreshConnector(doc, dynId, rootDyn.ConnectorIndex) ?? rootConn;
                        if (afterRevert is not null)
                            PipeAbsorptionApplier.TryApply(doc, dynId, afterRevert.OriginVec3, _rootRigidOffset);
                        doc.Regenerate();
                    });

                    SmartConLogger.Info("LockNetwork OFF: rigid reverted, absorb restored");
                }
                else
                {
                    SmartConLogger.Info("LockNetwork OFF: root moved since lock — skipping rigid revert");
                }

                _rootRigidApplied = false;
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
    /// The absorb can be reverted only while the root is untouched since Init and
    /// still sits at static in the absorbed pose: the root connector is the session
    /// one (no cycling) and its origin coincides with the static connector.
    /// </summary>
    private bool CanUndoRootAbsorb()
    {
        if (!_rootAbsorbApplied || _rootRigidApplied) return false;

        var rootDyn = _rootDynamicConnector ?? _ctx.DynamicConnector;
        if (rootDyn.ConnectorIndex != _ctx.DynamicConnector.ConnectorIndex) return false;

        var rootConn = _connSvc.RefreshConnector(_doc, rootDyn.OwnerElementId, rootDyn.ConnectorIndex);
        return rootConn is not null
            && VectorUtils.DistanceTo(rootConn.OriginVec3, _ctx.StaticConnector.OriginVec3) < RootPositionCheckEpsFt;
    }

    private void RefreshRootDynamicAfterLockToggle()
    {
        var rootDyn = _rootDynamicConnector ?? _ctx.DynamicConnector;
        _activeDynamic = _ctcManager.RefreshWithCtcOverride(
            _doc, rootDyn.OwnerElementId, rootDyn.ConnectorIndex) ?? _activeDynamic;
    }
}
