using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.PipeConnect.Services;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    // ── Element-wise chain queue (+/−) ────────────────────────────────────────

    /// <summary>
    /// Hard limit of chain elements processed in one session — safety net against
    /// runaway traversal. The flattened queue order guarantees a parent's index
    /// is always lower than its children's, so no graph cycle can loop forever;
    /// this limit only guards against pathological graphs (issue #137).
    /// </summary>
    private const int MaxChainElements = 500;

    /// <summary>
    /// Flattened chain queue in BFS discovery order (index 0 = root dynamic).
    /// Built once from <see cref="_chainGraph"/> in the constructor; the graph
    /// itself is immutable for the session lifetime.
    /// </summary>
    private IReadOnlyList<ChainQueueEntry>? _elementQueue;

    /// <summary>
    /// Numeric ids of elements attached in this session (root + queue[1..ChainDepth]).
    /// Passed to the attach handler so cross-edges (network loops) can be restored
    /// to already-attached neighbors right after the parent reconnect.
    /// </summary>
    private readonly HashSet<long> _attachedElementIds = [];

    /// <summary>
    /// True when the chain is sealed early (ADR-052): displacement fully absorbed,
    /// no resize work downstream, boundary reconnected. Further increments and
    /// ConnectAll are disabled — nothing left to do.
    /// </summary>
    private bool _chainSealed;

    /// <summary>
    /// True when the chain graph still has queue entries beyond the current depth
    /// that are not attached (chain not sealed, not disabled). Used by Connect to
    /// warn the user that finishing now would leave part of the network detached.
    /// </summary>
    private bool HasUnconnectedChainElements
        => _elementQueue is not null
        && !_chainSealed
        && !_chainDisabledByCycle
        && ChainDepth < _elementQueue.Count - 1;

    /// <summary>Total number of chain elements in the queue (excluding the root dynamic).</summary>
    private int TotalChainElementCount => _elementQueue?.Count - 1 ?? 0;

    /// <summary>Number of chain elements already attached (queue indices 1..ChainDepth).</summary>
    private int ConnectedChainElementCount => ChainDepth;

    /// <summary>
    /// True when the currently attached queue tail is the last element of its BFS
    /// level (the next entry starts a deeper level, or the queue is exhausted).
    /// Early sealing (ADR-052) is only evaluated at level boundaries — sealing
    /// mid-level would connect the level→level+1 boundary while sibling elements
    /// of the current level are still detached.
    /// </summary>
    private bool IsAtLevelBoundary
        => _elementQueue is not null
        && ChainDepth >= 0
        && (ChainDepth + 1 >= _elementQueue.Count
            || _elementQueue[ChainDepth + 1].Level > _elementQueue[ChainDepth].Level);

    /// <summary>
    /// True while ConnectAllChain runs the element-wise traversal. In bulk mode the
    /// expensive per-element dynamic switch (size reload via lookup tables, fitting
    /// re-evaluation, cycle state) is deferred to a single switch at the end —
    /// otherwise every attached element triggers a heavy LoadInitialSizes call and
    /// a 50-element ConnectAll stalls Revit for minutes.
    /// </summary>
    private bool _isBulkTraversal;

    /// <summary>
    /// Boundary edges connected by the active seal (ADR-052). Stored so the seal
    /// can be torn down (UnsealIfSealed) when the user edits the tail element or
    /// rolls the queue back — a seal is a transparent traversal optimization,
    /// never a lock on editing.
    /// </summary>
    private IReadOnlyList<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>? _sealedEdges;

    /// <summary>
    /// User's "Блокировать" toggle: when on, ConnectAll skips the quiet-seal and
    /// the deeper-radius check — the whole network is moved strictly as-is with
    /// no size compensation (rigid move fast path still applies).
    /// </summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _lockNetwork;

    /// <summary>
    /// Try to seal the remaining chain at the current boundary (ADR-052): when the
    /// displacement is fully absorbed and the downstream needs no resize work, the
    /// boundary is reconnected in one transaction and the rest of the network is
    /// left untouched. Called automatically after a manual "+" at a level boundary,
    /// from Init, and as the fast path of ConnectAllChain — so quiet networks are
    /// never traversed element-by-element, neither manually nor via ConnectAll.
    /// </summary>
    private bool TrySealAtCurrentBoundary()
    {
        if (_chainGraph is null || _elementQueue is null || _groupSession is null || !IsSessionActive)
            return false;
        if (!IsAtLevelBoundary)
            return false;

        int bfsLevel = _elementQueue[ChainDepth].Level;
        bool sealedNow = _chainOpHandler.TrySealQuietChain(
            _doc, _groupSession, _chainGraph, bfsLevel, out var edges);
        if (!sealedNow)
            return false;

        _chainSealed = true;
        _sealedEdges = edges;
        UpdateChainUI();
        SmartConLogger.Info($"Seal: chain sealed at level {bfsLevel}, boundary edges={edges.Count}, " +
            $"{_elementQueue.Count - 1 - ChainDepth} element(s) reconnected without churn");
        StatusMessage = string.Format(LocalizationService.GetString("Status_ChainSealed"), bfsLevel);
        return true;
    }

    /// <summary>
    /// Rigid-move fast path for ConnectAll: the whole remainder is translated as
    /// one body instead of per-element processing. Handles three outcomes:
    /// Connected / ConnectedViaReducer (chain becomes sealed) and Gapped (reducer
    /// missing — network placed with a 100 mm gap, warning dialog shown, chain
    /// stays unsealed so Connect warns about the detached remainder).
    /// </summary>
    private bool TryRigidMoveRemainderChain()
    {
        if (_chainGraph is null || _elementQueue is null || _groupSession is null || !IsSessionActive)
            return false;
        if (!IsAtLevelBoundary)
            return false;

        int bfsLevel = _elementQueue[ChainDepth].Level;
        var outcome = _chainOpHandler.TryRigidMoveRemainder(
            _doc, _groupSession, _chainGraph, bfsLevel, LockNetwork, out var edges);

        switch (outcome)
        {
            case ChainOperationHandler.RigidMoveOutcome.NotApplicable:
                return false;

            case ChainOperationHandler.RigidMoveOutcome.Gapped:
                SmartConLogger.Warn("RigidMove: network placed with 100 mm gap (reducer missing), NOT connected " +
                    "[Action: вставьте переход в месте зазора вручную или добавьте его в маппинг]");
                StatusMessage = LocalizationService.GetString("Status_NetworkGapped");
                _dialogSvc.ShowWarning(
                    LocalizationService.GetString("Dialog_GapNetwork_Title"),
                    LocalizationService.GetString("Dialog_GapNetwork_Message"));
                UpdateChainUI();
                return true;

            default: // Connected / ConnectedViaReducer
                _chainSealed = true;
                _sealedEdges = edges;
                UpdateChainUI();
                SmartConLogger.Info($"RigidMove: chain completed ({outcome}), " +
                    $"{_elementQueue.Count - 1 - ChainDepth} element(s) moved as one body");
                StatusMessage = outcome == ChainOperationHandler.RigidMoveOutcome.ConnectedViaReducer
                    ? LocalizationService.GetString("Status_NetworkMovedReducer")
                    : string.Format(LocalizationService.GetString("Status_NetworkMoved"),
                        _elementQueue.Count - 1 - ChainDepth);
                return true;
        }
    }

    /// <summary>
    /// Tear down an active seal before an operation that would invalidate it:
    /// rotation/resize/fitting/cycle of the tail element or a queue rollback.
    /// The sealed boundary edges are disconnected again (the network returns to
    /// "attached up to ChainDepth" state) so the operation never silently tears
    /// a sealed connection.
    /// </summary>
    private void UnsealIfSealed(string reason)
    {
        if (!_chainSealed) return;

        SmartConLogger.Info($"Unseal ({reason}): disconnecting {_sealedEdges?.Count ?? 0} sealed boundary edge(s)");
        try
        {
            _groupSession?.RunInTransaction(LocalizationService.GetString("Tx_ChainUnseal"), doc =>
            {
                if (_sealedEdges is not null)
                {
                    foreach (var (_, _, childId, childConnIdx) in _sealedEdges)
                    {
                        try
                        {
                            // Edges whose "child" is not part of the chain graph are
                            // elements we inserted (e.g. the rigid-move reducer) —
                            // delete them entirely instead of just disconnecting,
                            // otherwise unseal would leave an orphan in the model.
                            if (_chainGraph is not null
                                && !_chainGraph.Nodes.Contains(childId, ElementIdEqualityComparer.Instance))
                            {
                                SmartConLogger.Info($"Unseal: deleting inserted element id={childId.GetValue()}");
                                DisconnectAllConnectorsOf(doc, childId);
                                _fittingInsertSvc.DeleteElement(doc, childId);
                                _virtualCtcStore.RemoveForElement(childId);
                                continue;
                            }

                            _connSvc.DisconnectAllFromConnector(doc, childId, childConnIdx);
                        }
                        catch (Exception ex)
                        {
                            SmartConLogger.Warn($"Unseal: disconnect {childId.GetValue()}:{childConnIdx} failed: {ex.Message} " +
                                $"[Action: граница может остаться частично подключённой — проверьте соединения сети]");
                        }
                    }
                }
                doc.Regenerate();
            });
        }
        finally
        {
            _chainSealed = false;
            _sealedEdges = null;
            UpdateChainUI();
        }
    }

    [RelayCommand(CanExecute = nameof(CanIncrementChain))]
    private void IncrementChainDepth()
    {
        TryIncrementChainDepth(out _);
    }

    private bool TryIncrementChainDepth(out bool didWork)
    {
        didWork = false;
        using var _scope = SmartConLogger.BeginScope("EditorChain",
            ("Method", "IncrementChainDepth"));
        if (_elementQueue is null || _chainGraph is null || _chainSealed) return false;
        int nextIndex = ChainDepth + 1;
        if (nextIndex >= _elementQueue.Count || nextIndex > MaxChainElements) return false;

        var entry = _elementQueue[nextIndex];
        IsBusy = true;
        StatusMessage = string.Format(
            LocalizationService.GetString("Status_AttachingElement"), nextIndex, TotalChainElementCount);

        try
        {
            var result = _chainOpHandler.AttachSingleElement(
                _doc, _groupSession!, _chainGraph, _snapshotStore, _warmedElementIds, entry,
                _attachedElementIds, LockNetwork);

            if (result.Edge is null)
            {
                // Attach failed: the element was disconnected but never reconnected.
                // Roll it back to its snapshot so the network is not silently broken,
                // and do NOT advance the queue depth (Connect stays guarded).
                SmartConLogger.Warn($"Element {entry.ElementId.GetValue()} (queue index {nextIndex}) " +
                    $"could not be attached — rolling back to its original connections. " +
                    $"[Action: проверьте элемент в модели, затем повторите «+» или откатите очередь кнопкой «−»]");
                TryRollbackFailedAttach(entry);
                StatusMessage = string.Format(
                    LocalizationService.GetString("Error_AttachElement"), entry.ElementId.GetValue());
                return false;
            }

            didWork = result.DidWork;
            ChainDepth = nextIndex;
            _attachedElementIds.Add(entry.ElementId.GetValue());

            // Bulk mode (ConnectAll): defer the expensive dynamic switch to the end.
            if (!_isBulkTraversal)
                OnChainElementAttached(result);

            // Early seal (ADR-052): quiet remainder is reconnected in one
            // transaction — the user never walks idle elements manually.
            // Skipped when the network is locked ("Блокировать", issue #165):
            // the user attaches the remainder strictly element-by-element.
            if (!LockNetwork)
                TrySealAtCurrentBoundary();

            UpdateChainUI();
            if (!_chainSealed)
            {
                StatusMessage = string.Format(
                    LocalizationService.GetString(didWork ? "Status_ElementAttached" : "Status_ElementAttachedIdle"),
                    nextIndex, TotalChainElementCount);
            }
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Error: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_Chain"), ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Single dynamic switch after a bulk (ConnectAll) traversal: the active
    /// dynamic becomes the queue tail once, with one size/fitting reload —
    /// instead of a heavy reload per attached element.
    /// </summary>
    private void FinalizeBulkDynamicSwitch()
    {
        if (ChainDepth <= 0 || _elementQueue is null) return;

        SaveActivePointState();
        _activePointIndex = ChainDepth;
        LoadPointState(ChainDepth);
        RefreshActiveDynamicForCurrentPoint();
        RefreshAfterDynamicSwitch();

        SmartConLogger.Info($"Bulk traversal finished: active dynamic → element {_activeDynamic?.OwnerElementId.GetValue()} " +
            $"(queue {ChainDepth}/{TotalChainElementCount})");
    }

    private bool CanIncrementChain()
        => IsSessionActive && !IsBusy
        && _elementQueue is not null
        && !_chainDisabledByCycle
        && !_chainSealed
        && ChainDepth < _elementQueue.Count - 1
        && ChainDepth < MaxChainElements;

    [RelayCommand(CanExecute = nameof(CanDecrementChain))]
    private void DecrementChainDepth()
    {
        using var _scope = SmartConLogger.BeginScope("EditorChain",
            ("Method", "DecrementChainDepth"));
        if (_elementQueue is null || _chainGraph is null || ChainDepth <= 0) return;

        var entry = _elementQueue[ChainDepth];
        IsBusy = true;
        StatusMessage = string.Format(
            LocalizationService.GetString("Status_RollingBackElement"), ChainDepth);

        try
        {
            UnsealIfSealed("откат элемента");

            _chainOpHandler.DetachSingleElement(
                _doc, _groupSession!, _chainGraph, _snapshotStore, entry);

            int detachedIndex = ChainDepth;
            ChainDepth--;
            _attachedElementIds.Remove(entry.ElementId.GetValue());
            _chainSealed = false;

            OnChainElementDetached(entry, detachedIndex);

            UpdateChainUI();
            StatusMessage = string.Format(
                LocalizationService.GetString("Status_ElementDetached"), detachedIndex);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Error: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_Rollback"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDecrementChain()
        => IsSessionActive && !IsBusy && ChainDepth > 0;

    /// <summary>
    /// Best-effort rollback of an element whose attach failed (no parent edge or
    /// dead parent connector). Restores its original connections from the snapshot
    /// taken at the start of the failed attach. Failures are logged and swallowed —
    /// the session must stay alive so the user can detach the queue manually.
    /// </summary>
    private void TryRollbackFailedAttach(ChainQueueEntry entry)
    {
        try
        {
            _chainOpHandler.DetachSingleElement(
                _doc, _groupSession!, _chainGraph!, _snapshotStore, entry);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Rollback of failed attach for element {entry.ElementId.GetValue()} failed: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectAllChain))]
    private void ConnectAllChain()
    {
        if (_elementQueue is null) return;

        IsBusy = true;
        StatusMessage = LocalizationService.GetString("Status_ConnectingNetwork");

        try
        {
            // Fast path 1 (ADR-052): quiet remainder — reconnect in one transaction.
            // Skipped when the user locked the network ("Блокировать"): even a
            // quiet network is then moved strictly as-is.
            if (!LockNetwork && TrySealAtCurrentBoundary())
                return;

            // Fast path 2: rigid move — the whole remainder is translated as one
            // body (one MoveElements call) and the boundary is reconnected, with
            // a reducer on DN mismatch or a 100 mm gap when the reducer is missing.
            if (TryRigidMoveRemainderChain())
                return;

            int targetIndex = _elementQueue.Count - 1;

            ChainTraversalResult result;
            _isBulkTraversal = true;
            try
            {
                result = ChainTraversalRunner.Run(
                    ChainDepth, targetIndex, MaxChainElements,
                    () => TryIncrementChainDepth(out _) ? ChainDepth : (int?)null);
            }
            finally
            {
                _isBulkTraversal = false;
                FinalizeBulkDynamicSwitch();
            }

            if (_chainSealed)
            {
                // Seal happened mid-traversal (inside TryIncrementChainDepth) —
                // the network is fully reconnected, so a "StepFailed" from the
                // runner is not a failure: the step simply refuses to continue
                // on a sealed chain. Status was already set by the seal itself.
                SmartConLogger.Info("ConnectAllChain finished: chain sealed mid-traversal, network fully reconnected");
                return;
            }

            switch (result.StopReason)
            {
                case ChainTraversalStopReason.Completed:
                    StatusMessage = string.Format(
                        LocalizationService.GetString("Status_ElementsConnected"), result.Processed);
                    break;
                case ChainTraversalStopReason.NoProgress:
                    SmartConLogger.Warn($"ConnectAllChain stopped: ChainDepth did not advance at index {result.FinalDepth}. " +
                        $"[Action: сообщите разработчикам — шаг обхода завершился без продвижения, зацикливание предотвращено]");
                    break;
                case ChainTraversalStopReason.StepFailed:
                    // StatusMessage с текстом ошибки уже установлен в TryIncrementChainDepth.
                    SmartConLogger.Warn($"ConnectAllChain stopped at index {result.FinalDepth} after {result.Processed} element(s): step failed. " +
                        $"[Action: проверьте сообщение об ошибке в статусной строке и повторите подключение]");
                    break;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Error: {ex.Message}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnectAllChain()
        => IsSessionActive && !IsBusy
        && _elementQueue is not null
        && !_chainDisabledByCycle
        && !_chainSealed
        && ChainDepth < _elementQueue.Count - 1
        && ChainDepth < MaxChainElements;

    private void UpdateChainUI()
    {
        HasChain = _elementQueue is not null
            && _elementQueue.Count > 1
            && !_chainDisabledByCycle;

        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
        ConnectAllChainCommand.NotifyCanExecuteChanged();

        // Seal state gates every editing command — keep their enabled state in sync.
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        ChangeDynamicSizeCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        InsertReducerCommand.NotifyCanExecuteChanged();
        ReflectFittingCtcCommand.NotifyCanExecuteChanged();
        ReflectReducerCtcCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
    }
}
