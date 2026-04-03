using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// ViewModel модального окна PipeConnectEditor.
/// Все изменения модели выполняются внутри единой TransactionGroup.
/// Cancel → group.RollBack() — полный откат всех изменений.
/// Connect → group.Assimilate() — одна запись Undo.
/// </summary>
public sealed partial class PipeConnectEditorViewModel : ObservableObject
{
    private readonly ITransactionService      _txService;
    private readonly Document                 _doc;
    private readonly IConnectorService        _connSvc;
    private readonly ITransformService        _transformSvc;
    private readonly IFittingInsertService    _fittingInsertSvc;
    private readonly IParameterResolver       _paramResolver;
    private readonly PipeConnectSessionContext _ctx;

    private ITransactionGroupSession? _groupSession;
    private ElementId?      _currentFittingId;
    private ConnectorProxy? _activeDynamic;
    private ConnectorProxy? _activeFittingConn2;
    private bool            _isClosing;

    private List<ConnectorProxy>  _allDynamicConnectors = [];
    private readonly HashSet<int> _visitedConnectorIndices = [];
    private int                   _connectorCyclePos = 0;

    [ObservableProperty] private bool    _moveEntireChain;
    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string  _statusMessage = "Инициализация…";
    [ObservableProperty] private FittingCardItem? _selectedFitting;
    [ObservableProperty] private bool    _isSessionActive;

    public ObservableCollection<FittingCardItem> AvailableFittings { get; } = [];

    [ObservableProperty] private int _rotationAngleDeg = 15;

    public event Action? RequestClose;

    public PipeConnectEditorViewModel(
        PipeConnectSessionContext  ctx,
        Document                   doc,
        ITransactionService        txService,
        IConnectorService          connSvc,
        ITransformService          transformSvc,
        IFittingInsertService      fittingInsertSvc,
        IParameterResolver         paramResolver)
    {
        _ctx              = ctx;
        _doc              = doc;
        _txService        = txService;
        _connSvc          = connSvc;
        _transformSvc     = transformSvc;
        _fittingInsertSvc = fittingInsertSvc;
        _paramResolver    = paramResolver;
        _activeDynamic    = ctx.DynamicConnector;

        bool hasMandatoryFittings = ctx.ProposedFittings.Count > 0 &&
            ctx.ProposedFittings.Any(r => !r.IsDirectConnect && r.FittingFamilies.Count > 0);

        if (!hasMandatoryFittings)
            AvailableFittings.Add(new FittingCardItem(new FittingMappingRule
            {
                FromType        = ctx.StaticConnector.ConnectionTypeCode,
                ToType          = ctx.DynamicConnector.ConnectionTypeCode,
                IsDirectConnect = true
            }));

        foreach (var rule in ctx.ProposedFittings)
            if (!rule.IsDirectConnect && rule.FittingFamilies.Count > 0)
                AvailableFittings.Add(new FittingCardItem(rule));

        SelectedFitting = AvailableFittings.Count > 0 ? AvailableFittings[0] : null;
    }

    /// <summary>
    /// Вызывается из View.Loaded. Открывает TransactionGroup, применяет S3+S4, вставляет фитинг.
    /// </summary>
    public void Init()
    {
        _groupSession = _txService.BeginGroupSession("PipeConnect");
        IsSessionActive = true;

        try
        {
            // S4: подгонка размера (если нужно)
            if (_ctx.ParamTargetRadius is { } targetRadius)
            {
                _groupSession.RunInTransaction("PipeConnect — Подгонка размера", doc =>
                {
                    _paramResolver.TrySetConnectorRadius(
                        doc,
                        _ctx.DynamicConnector.OwnerElementId,
                        _ctx.DynamicConnector.ConnectorIndex,
                        targetRadius);
                    doc.Regenerate();
                });
                _activeDynamic = _connSvc.RefreshConnector(
                    _doc, _ctx.DynamicConnector.OwnerElementId, _ctx.DynamicConnector.ConnectorIndex)
                    ?? _ctx.DynamicConnector;
            }

            // Загрузить коннекторы
            var conns = GetFreeConnectorsSnapshot();
            InitConnectorCycle(conns);

            // Вставить фитинг по умолчанию
            var defaultFitting = SelectedFitting;
            if (defaultFitting is not null && !defaultFitting.IsDirectConnect)
            {
                StatusMessage = "Установка фитинга…";
                InsertFittingSilent(defaultFitting);
            }
            else
            {
                StatusMessage = "Готово к соединению";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка инициализации: {ex.Message}";
            _groupSession.RollBack();
            _groupSession = null;
            IsSessionActive = false;
            RequestClose?.Invoke();
        }
    }

    // ── Rotation ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RotateLeft()  => ExecuteRotate(-RotationAngleDeg);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RotateRight() => ExecuteRotate(+RotationAngleDeg);

    private void ExecuteRotate(int angleDeg)
    {
        IsBusy = true;
        try
        {
            _groupSession!.RunInTransaction("PipeConnect — Поворот", doc =>
            {
                var dynId      = _ctx.DynamicConnector.OwnerElementId;
                var axisOrigin = _ctx.StaticConnector.OriginVec3;
                var axisDir    = _ctx.StaticConnector.BasisZVec3;
                var radians    = angleDeg * System.Math.PI / 180.0;

                var idsToRotate = new List<ElementId> { dynId };
                if (_currentFittingId is not null)
                    idsToRotate.Add(_currentFittingId);

                var activeIdx = _activeDynamic?.ConnectorIndex
                             ?? _ctx.DynamicConnector.ConnectorIndex;
                var dynElem = doc.GetElement(dynId);
                ConnectorManager? cm = dynElem switch
                {
                    FamilyInstance fi => fi.MEPModel?.ConnectorManager,
                    MEPCurve mc       => mc.ConnectorManager,
                    _                 => null
                };
                if (cm is not null)
                {
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c.ConnectorType == ConnectorType.Curve) continue;
                        if ((int)c.Id == activeIdx) continue;
                        if (!c.IsConnected) continue;
                        foreach (Connector refConn in c.AllRefs)
                        {
                            var refId = refConn.Owner?.Id;
                            if (refId is not null && refId != dynId)
                                idsToRotate.Add(refId);
                        }
                    }
                }

                _transformSvc.RotateElements(doc, idsToRotate, axisOrigin, axisDir, radians);
                doc.Regenerate();
            });

            StatusMessage = $"Повёрнуто на {angleDeg:+#;-#;0}°";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка поворота: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Cycle connector ───────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanCycleConnector))]
    private void CycleConnector()
    {
        if (_allDynamicConnectors.Count <= 1) return;

        var target = FindNextUnvisitedConnector();
        if (target is null) return;

        IsBusy = true;
        StatusMessage = "Переключение коннектора…";

        try
        {
            var alignTarget = _activeFittingConn2 ?? _ctx.StaticConnector;

            _groupSession!.RunInTransaction("PipeConnect — Смена коннектора", doc =>
            {
                var freshTarget = _connSvc.RefreshConnector(
                    doc, target.OwnerElementId, target.ConnectorIndex) ?? target;

                var reAlign = ConnectorAligner.ComputeAlignment(
                    alignTarget.OriginVec3, alignTarget.BasisZVec3, alignTarget.BasisXVec3,
                    freshTarget.OriginVec3, freshTarget.BasisZVec3, freshTarget.BasisXVec3);

                var dynId = target.OwnerElementId;
                if (!VectorUtils.IsZero(reAlign.InitialOffset))
                    _transformSvc.MoveElement(doc, dynId, reAlign.InitialOffset);
                if (reAlign.BasisZRotation is { } bz)
                    _transformSvc.RotateElement(doc, dynId, reAlign.RotationCenter, bz.Axis, bz.AngleRadians);
                if (reAlign.BasisXSnap is { } bx)
                    _transformSvc.RotateElement(doc, dynId, reAlign.RotationCenter, bx.Axis, bx.AngleRadians);

                doc.Regenerate();
                var r = _connSvc.RefreshConnector(doc, dynId, target.ConnectorIndex);
                if (r is not null)
                {
                    var corr = alignTarget.OriginVec3 - r.OriginVec3;
                    if (!VectorUtils.IsZero(corr))
                        _transformSvc.MoveElement(doc, dynId, corr);
                }
                doc.Regenerate();
                _activeDynamic = _connSvc.RefreshConnector(doc, dynId, target.ConnectorIndex);
            });

            _visitedConnectorIndices.Add(target.ConnectorIndex);
            StatusMessage = "Коннектор изменён";
            CycleConnectorCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCycleConnector() => IsSessionActive && !IsBusy && _allDynamicConnectors.Count > 1;

    private ConnectorProxy? FindNextUnvisitedConnector()
    {
        int count = _allDynamicConnectors.Count;
        if (count == 0) return null;

        for (int i = 0; i < count; i++)
        {
            int idx  = (_connectorCyclePos + i) % count;
            var conn = _allDynamicConnectors[idx];
            if (!_visitedConnectorIndices.Contains(conn.ConnectorIndex))
            {
                _connectorCyclePos = (idx + 1) % count;
                return conn;
            }
        }

        _visitedConnectorIndices.Clear();
        var first = _allDynamicConnectors[_connectorCyclePos % count];
        _connectorCyclePos = (_connectorCyclePos + 1) % count;
        return first;
    }

    // ── Insert fitting ────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanInsertFitting))]
    private void InsertFitting()
    {
        if (SelectedFitting is null) return;

        IsBusy = true;
        StatusMessage = "Вставка фитинга…";

        try
        {
            InsertFittingSilent(SelectedFitting);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка вставки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InsertFittingSilent(FittingCardItem fitting)
    {
        if (fitting.IsDirectConnect)
        {
            _groupSession!.RunInTransaction("PipeConnect — Прямое соединение", doc =>
            {
                doc.Regenerate();
            });
            StatusMessage = "Прямое соединение";
            return;
        }

        var primary = fitting.PrimaryFitting;
        if (primary is null)
        {
            StatusMessage = "Нет данных о семействе фитинга";
            return;
        }

        // Транзакция 1: удалить старый фитинг + вставить новый + выровнять
        ElementId? insertedId = null;
        ConnectorProxy? fitConn2 = null;

        _groupSession!.RunInTransaction("PipeConnect — Вставка фитинга", doc =>
        {
            if (_currentFittingId is not null)
            {
                _fittingInsertSvc.DeleteElement(doc, _currentFittingId);
                _currentFittingId   = null;
                _activeFittingConn2 = null;
            }

            insertedId = _fittingInsertSvc.InsertFitting(
                doc, primary.FamilyName, primary.SymbolName, _ctx.StaticConnector.Origin);

            if (insertedId is null) return;

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedId, _ctx.StaticConnector, _transformSvc, _connSvc);

            if (fitConn2 is not null && _activeDynamic is not null)
            {
                var activeProxy = _connSvc.RefreshConnector(
                    doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                    ?? _activeDynamic;

                var offset = fitConn2.OriginVec3 - activeProxy.OriginVec3;
                if (!VectorUtils.IsZero(offset))
                    _transformSvc.MoveElement(doc, _activeDynamic.OwnerElementId, offset);
            }

            doc.Regenerate();
        });

        if (insertedId is null)
        {
            StatusMessage = $"Семейство '{primary.FamilyName}' не найдено в проекте";
            return;
        }

        _currentFittingId   = insertedId;
        _activeFittingConn2 = fitConn2;
        StatusMessage = $"Вставлен: {fitting.DisplayName}";

        // Транзакция 2: подгонка размера фитинга (отдельная транзакция после коммита вставки)
        SizeFittingConnectors(_doc, insertedId, fitConn2);
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void Connect()
    {
        IsBusy = true;
        StatusMessage = "Соединение…";

        try
        {
            _groupSession!.RunInTransaction("PipeConnect — ConnectTo", doc =>
            {
                if (_currentFittingId is not null)
                {
                    var fConns = _connSvc.GetAllFreeConnectors(doc, _currentFittingId);
                    var fc1 = fConns
                        .OrderBy(c => (_ctx.StaticConnector.Origin - c.Origin).GetLength())
                        .FirstOrDefault();

                    if (fc1 is not null)
                        _connSvc.ConnectTo(doc,
                            _ctx.StaticConnector.OwnerElementId, _ctx.StaticConnector.ConnectorIndex,
                            _currentFittingId, fc1.ConnectorIndex);

                    var fc2 = fConns.FirstOrDefault(c => c.ConnectorIndex != (fc1?.ConnectorIndex ?? -1));
                    var dyn = _activeDynamic ?? _ctx.DynamicConnector;
                    var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
                    if (fc2 is not null)
                        _connSvc.ConnectTo(doc, _currentFittingId, fc2.ConnectorIndex,
                            dynR.OwnerElementId, dynR.ConnectorIndex);
                }
                else
                {
                    var dyn = _activeDynamic ?? _ctx.DynamicConnector;
                    var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
                    _connSvc.ConnectTo(doc,
                        _ctx.StaticConnector.OwnerElementId, _ctx.StaticConnector.ConnectorIndex,
                        dynR.OwnerElementId, dynR.ConnectorIndex);
                }
                doc.Regenerate();
            });

            _groupSession.Assimilate();
            _groupSession = null;
            StatusMessage = "Соединение выполнено";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsSessionActive = false;
            RequestClose?.Invoke();
        }
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [RelayCommand]
    public void Cancel()
    {
        if (_isClosing) return;
        _isClosing = true;

        if (!IsSessionActive)
        {
            RequestClose?.Invoke();
            return;
        }

        try
        {
            _groupSession?.RollBack();
        }
        catch { /* best-effort cancel */ }
        finally
        {
            _groupSession = null;
            IsSessionActive = false;
            IsBusy = false;
            RequestClose?.Invoke();
        }
    }

    public bool IsClosing => _isClosing;

    // ── CanExecute helpers ────────────────────────────────────────────────────

    private bool CanOperate()       => IsSessionActive && !IsBusy;
    private bool CanInsertFitting() => IsSessionActive && !IsBusy && SelectedFitting is not null;

    partial void OnIsBusyChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void SizeFittingConnectors(Document doc, ElementId fittingId, ConnectorProxy? fitConn2)
    {
        try
        {
            var allConns = _connSvc.GetAllFreeConnectors(doc, fittingId).ToList();
            if (allConns.Count == 0) return;

            int conn2Idx = fitConn2?.ConnectorIndex ?? -1;
            int conn1Idx = allConns.FirstOrDefault(c => c.ConnectorIndex != conn2Idx)?.ConnectorIndex ?? -1;

            var depsByIdx = new Dictionary<int, IReadOnlyList<ParameterDependency>>();
            foreach (var c in allConns)
                depsByIdx[c.ConnectorIndex] = _paramResolver.GetConnectorRadiusDependencies(doc, fittingId, c.ConnectorIndex);

            bool usePairMode = conn1Idx >= 0 && conn2Idx >= 0
                && depsByIdx.TryGetValue(conn1Idx, out var d1) && d1.Count > 0 && !d1[0].IsInstance
                && depsByIdx.TryGetValue(conn2Idx, out var d2) && d2.Count > 0 && !d2[0].IsInstance;

            if (usePairMode)
            {
                double currentDynRadius = _activeDynamic?.Radius ?? _ctx.DynamicConnector.Radius;
                double achievedDynRadius = currentDynRadius;

                _groupSession!.RunInTransaction("PipeConnect — Размер фитинга", txDoc =>
                {
                    var (_, dynR) = _paramResolver.TrySetFittingTypeForPair(
                        txDoc, fittingId,
                        conn1Idx, _ctx.StaticConnector.Radius,
                        conn2Idx, currentDynRadius);
                    achievedDynRadius = dynR;
                    txDoc.Regenerate();
                });

                const double eps = 1e-6;
                if (System.Math.Abs(achievedDynRadius - currentDynRadius) > eps)
                {
                    var dynId      = _ctx.DynamicConnector.OwnerElementId;
                    var dynConnIdx = _ctx.DynamicConnector.ConnectorIndex;
                    _groupSession.RunInTransaction("PipeConnect — Подгонка dynamic под фитинг", txDoc =>
                    {
                        _paramResolver.TrySetConnectorRadius(txDoc, dynId, dynConnIdx, achievedDynRadius);
                        txDoc.Regenerate();
                    });
                }
            }
            else
            {
                double currentDynRadius = _activeDynamic?.Radius ?? _ctx.DynamicConnector.Radius;

                _groupSession!.RunInTransaction("PipeConnect — Размер фитинга", txDoc =>
                {
                    foreach (var c in allConns)
                    {
                        double targetRadius = c.ConnectorIndex == conn2Idx
                            ? currentDynRadius
                            : _ctx.StaticConnector.Radius;
                        _paramResolver.TrySetConnectorRadius(txDoc, fittingId, c.ConnectorIndex, targetRadius);
                    }
                    txDoc.Regenerate();
                });
            }

            RealignAfterSizing(doc, fittingId);
        }
        catch { /* best-effort */ }
    }

    private ConnectorProxy? RealignAfterSizing(Document doc, ElementId fittingId)
    {
        ConnectorProxy? newFitConn2 = null;
        try
        {
            _groupSession!.RunInTransaction("PipeConnect — Выравнивание после размера", txDoc =>
            {
                newFitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                    txDoc, fittingId, _ctx.StaticConnector, _transformSvc, _connSvc);

                if (newFitConn2 is not null && _activeDynamic is not null)
                {
                    var dynProxy = _connSvc.RefreshConnector(
                        txDoc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                        ?? _activeDynamic;

                    var offset = newFitConn2.OriginVec3 - dynProxy.OriginVec3;
                    if (!VectorUtils.IsZero(offset))
                        _transformSvc.MoveElement(txDoc, _activeDynamic.OwnerElementId, offset);

                    txDoc.Regenerate();

                    _activeDynamic = _connSvc.RefreshConnector(
                        txDoc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                        ?? _activeDynamic;
                }

                txDoc.Regenerate();
            });
        }
        catch { /* best-effort */ }
        return newFitConn2;
    }

    private List<ConnectorProxy> GetFreeConnectorsSnapshot()
    {
        try
        {
            return _connSvc.GetAllFreeConnectors(_doc, _ctx.DynamicConnector.OwnerElementId).ToList();
        }
        catch { return []; }
    }

    private void InitConnectorCycle(List<ConnectorProxy> connectors)
    {
        _allDynamicConnectors = connectors;
        _visitedConnectorIndices.Clear();
        _connectorCyclePos = 0;

        var active = _activeDynamic ?? _ctx.DynamicConnector;
        _visitedConnectorIndices.Add(active.ConnectorIndex);
        int idx = connectors.FindIndex(c => c.ConnectorIndex == active.ConnectorIndex);
        _connectorCyclePos = (System.Math.Max(idx, 0) + 1) % System.Math.Max(1, connectors.Count);

        CycleConnectorCommand.NotifyCanExecuteChanged();
    }
}
