using System.Collections.ObjectModel;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    [RelayCommand(CanExecute = nameof(CanEditOperations))]
    private void RotateLeft() => ExecuteRotate(+RotationAngleDeg);

    [RelayCommand(CanExecute = nameof(CanEditOperations))]
    private void RotateRight() => ExecuteRotate(-RotationAngleDeg);

    /// <summary>
    /// Set the active dynamic upright: rotate it (with its point fitting/reducer)
    /// around the parent connector axis so the FREE connector's BasisY — the
    /// family's "height" direction — lands exactly on the projection of world up
    /// (global +Z) onto the connector plane. Falls back to the horizontal grid
    /// (Y/X) for vertical axes. Connector BasisY is used instead of the family
    /// transform basis: it always lies in the rotation plane (never degenerates)
    /// and reflects flips — this is what makes the reset work for every family.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditOperations))]
    private void ZeroRotation()
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "ZeroRotation"));
        if (_activeDynamic is null) return;

        IsBusy = true;
        try
        {
            UnsealIfSealed("установка вертикально");

            bool applied = false;
            _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_ZeroRotation"), doc =>
            {
                var dyn = RefreshConnectorSafe(_activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                          ?? _activeDynamic;
                var axis = ActiveUpstreamConnector;

                var upright = ConnectorAligner.ComputeUprightRotation(axis.BasisZVec3, dyn.BasisYVec3);
                if (upright is null) return;

                var idsToRotate = new List<ElementId> { _activeDynamic.OwnerElementId };
                if (_currentFittingId is not null) idsToRotate.Add(_currentFittingId);
                if (_primaryReducerId is not null) idsToRotate.Add(_primaryReducerId);

                _transformSvc.RotateElements(doc, idsToRotate, axis.OriginVec3, upright.Axis, upright.AngleRadians);
                doc.Regenerate();
                applied = true;

                SmartConLogger.Info($"ZeroRotation applied: {upright.AngleRadians * 180.0 / System.Math.PI:F2}° " +
                    $"around axis owner={axis.OwnerElementId.GetValue()}");
            });

            _activeDynamic = _ctcManager.RefreshWithCtcOverride(
                _doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                ?? _activeDynamic;
            UpdateDynamicInfoPanel();
            LogFinalRotationAngles();

            StatusMessage = applied
                ? LocalizationService.GetString("Status_RotationZeroed")
                : string.Format(LocalizationService.GetString("Status_RotationAlreadyZero"), GetReferenceAxisName());
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_Rotate"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ExecuteRotate(int angleDeg)
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "ExecuteRotate"),
            ("Angle", angleDeg));
        if (_activeDynamic is null) return;

        IsBusy = true;
        try
        {
            // Lock-network mode with a sealed chain: the whole sealed remainder
            // rotates together with the active dynamic as one rigid body — the
            // seal stays intact (common-axis rotation preserves connections).
            IReadOnlyList<ElementId>? rigidSubtreeIds = null;
            if (LockNetwork && _chainSealed && _elementQueue is not null && _chainGraph is not null)
            {
                var subtree = new List<ElementId>();
                for (int i = ChainDepth + 1; i < _elementQueue.Count; i++)
                    subtree.Add(_elementQueue[i].ElementId);

                // Inserted elements of the seal (e.g. rigid-move reducer) are not
                // graph nodes — rotate them with the body as well.
                if (_sealedEdges is not null)
                {
                    foreach (var (_, _, childId, _) in _sealedEdges)
                    {
                        if (!_chainGraph.Nodes.Contains(childId, ElementIdEqualityComparer.Instance))
                            subtree.Add(childId);
                    }
                }

                if (subtree.Count > 0)
                    rigidSubtreeIds = subtree;
            }
            else
            {
                UnsealIfSealed("поворот");
            }

            _rotationHandler.ExecuteRotation(
                _doc, _groupSession!, _activeDynamic, ActiveUpstreamConnector,
                _currentFittingId, _primaryReducerId, angleDeg, rigidSubtreeIds);

            // After a rigid-body rotation the attached part did not rotate —
            // loop edges crossing the boundary may have drifted; restore them.
            if (rigidSubtreeIds is not null && _chainGraph is not null && _elementQueue is not null)
                _chainOpHandler.RestoreCrossEdgesAfterRigidRotation(
                    _doc, _groupSession!, _chainGraph, _elementQueue[ChainDepth].Level);

            _activeDynamic = _ctcManager.RefreshWithCtcOverride(
                _doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                ?? _activeDynamic;
            UpdateDynamicInfoPanel();
            LogFinalRotationAngles();

            StatusMessage = string.Format(LocalizationService.GetString("Status_Rotated"), angleDeg);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_Rotate"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeDynamicSize))]
    private void ChangeDynamicSize()
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "ChangeDynamicSize"));
        if (SelectedDynamicSize is null || SelectedDynamicSize.IsAutoSelect) return;

        IsBusy = true;
        StatusMessage = string.Format(LocalizationService.GetString("Status_ChangingSizeTo"), SelectedDynamicSize.DisplayName);
        SmartConLogger.Info($"Attempting size change to {SelectedDynamicSize.DisplayName} " +
            $"(radius={SelectedDynamicSize.Radius * FeetToMm:F2} mm, source={SelectedDynamicSize.Source}, " +
            $"allRadii={SelectedDynamicSize.AllConnectorRadii.Count} коннекторов)");

        try
        {
            UnsealIfSealed("смена размера");

            var result = _sizeHandler.ChangeSize(
                _doc, _groupSession!, _ctx, SelectedDynamicSize,
                _activeDynamic!, ActiveUpstreamConnector, _currentFittingId, _primaryReducerId);

            _activeDynamic = result.ActiveDynamic;
            _userManuallyChangedSize = result.UserManuallyChangedSize;
            UpdateDynamicInfoPanel();

            StatusMessage = string.Format(LocalizationService.GetString("Status_SizeChangedTo"), SelectedDynamicSize.DisplayName);

            if (_currentFittingId is not null)
            {
                StatusMessage = LocalizationService.GetString("Status_UpdatingFitting");
                var currentFitting = SelectedFitting;
                if (currentFitting is not null && !currentFitting.IsDirectConnect)
                {
                    SmartConLogger.Info($"Auto-update fitting: {currentFitting.DisplayName}");
                    InsertFittingSilent(currentFitting, adjustDynamicToFit: false);
                }
            }

            if (_primaryReducerId is not null)
            {
                SmartConLogger.Info($"Auto-update reducer (id={_primaryReducerId})");
                var reducerUpstream = (_currentFittingId is not null && _activeFittingConn2 is not null)
                    ? _activeFittingConn2
                    : ActiveUpstreamConnector;
                var newReducerConn2 = SizeFittingConnectors(_doc, _primaryReducerId, null, adjustDynamicToFit: false, reducerUpstream);
                if (newReducerConn2 is not null && _activeDynamic is not null)
                {
                    _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_PositionAfterReducer"), doc =>
                    {
                        var dynProxy = _connSvc.RefreshConnector(
                            doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                            ?? _activeDynamic;
                        var offset = newReducerConn2.OriginVec3 - dynProxy.OriginVec3;
                        if (!SmartCon.Core.Math.VectorUtils.IsZero(offset))
                            PipeAbsorptionApplier.MoveOrAbsorb(
                                doc, _transformSvc, _activeDynamic.OwnerElementId, dynProxy.OriginVec3, offset);
                        doc.Regenerate();
                    });
                }
            }

            if (result.NeedsPrimaryReducer && _currentFittingId is null && _primaryReducerId is null)
            {
                _needsPrimaryReducer = true;

                if (AvailableReducers.Count > 0)
                {
                    SelectedReducer = AvailableReducers[0];
                    IsReducerVisible = true;
                    StatusMessage = LocalizationService.GetString("Status_InsertingReducer");
                    InsertReducerSilent();
                }
                else
                {
                    IsReducerVisible = true;
                    SmartConLogger.Warn("Reducer needed but no reducer families found [Action: добавьте семейство редуктора в mapping (Настройки → Правила)]");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Error: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_ChangeSize"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChangeDynamicSize() =>
        IsSessionActive && !IsBusy &&
        SelectedDynamicSize is not null && !SelectedDynamicSize.IsAutoSelect;

    partial void OnSelectedDynamicSizeChanged(FamilySizeOption? value)
    {
        ChangeDynamicSizeCommand.NotifyCanExecuteChanged();
    }
}
