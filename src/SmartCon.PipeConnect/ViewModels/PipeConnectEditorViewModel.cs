using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Events;

namespace SmartCon.PipeConnect.ViewModels;

/// <summary>
/// ViewModel немодального окна PipeConnectEditor (Phase 8, S6).
/// К моменту открытия окна S3+S4 уже выполнены PipeConnectCommand.
/// Каждая операция — отдельная Transaction (без долгоживущей TransactionGroup).
/// Отмена: удаляет вставленный фитинг и откатывает накопленный поворот.
/// </summary>
public sealed partial class PipeConnectEditorViewModel : ObservableObject
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly ITransactionService      _txService;
    private readonly IConnectorService        _connSvc;
    private readonly ITransformService        _transformSvc;
    private readonly IFittingInsertService    _fittingInsertSvc;
    private readonly IParameterResolver       _paramResolver;
    private readonly PipeConnectExternalEvent _eventHandler;

    // ── Session state ─────────────────────────────────────────────────────────
    private readonly PipeConnectSessionContext _ctx;
    private ElementId?      _currentFittingId;
    private ConnectorProxy? _activeDynamic;
    private ConnectorProxy? _activeFittingConn2;   // fitting-коннектор со стороны dynamic
    private bool            _isClosing;

    // ── Connector cycling ─────────────────────────────────────────────────────
    private List<ConnectorProxy>  _allDynamicConnectors = [];
    private readonly HashSet<int> _visitedConnectorIndices = [];
    private int                   _connectorCyclePos = 0;

    // ── Undo tracking ─────────────────────────────────────────────────────────
    private double _totalRotationDeg = 0.0;   // для отмены поворотов

    // ── Observable properties ─────────────────────────────────────────────────
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
        ITransactionService        txService,
        IConnectorService          connSvc,
        ITransformService          transformSvc,
        IFittingInsertService      fittingInsertSvc,
        IParameterResolver         paramResolver,
        PipeConnectExternalEvent   eventHandler)
    {
        _ctx              = ctx;
        _txService        = txService;
        _connSvc          = connSvc;
        _transformSvc     = transformSvc;
        _fittingInsertSvc = fittingInsertSvc;
        _paramResolver    = paramResolver;
        _eventHandler     = eventHandler;
        _activeDynamic    = ctx.DynamicConnector;

        // ── Список фитингов ─────────────────────────────────────────────────
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

    // ── Initialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается из View.Loaded.
    /// Сразу вставляет фитинг с высшим приоритетом (если задан маппинг).
    /// </summary>
    public void OnWindowLoaded()
    {
        // Активируем сразу — каждая операция сама открывает Transaction
        IsSessionActive = true;

        var defaultFitting = SelectedFitting;
        if (defaultFitting is null || defaultFitting.IsDirectConnect)
        {
            StatusMessage = "Готово к соединению";
            // Загрузить список коннекторов без Revit-транзакции
            _eventHandler.Raise(app =>
            {
                var conns = GetFreeConnectorsSnapshot(app.ActiveUIDocument.Document);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    InitConnectorCycle(conns);
                    StatusMessage = "Готово к соединению";
                });
            });
            return;
        }

        // Есть фитинг по умолчанию — вставляем сразу
        IsBusy = true;
        StatusMessage = "Установка фитинга…";

        _eventHandler.Raise(app =>
        {
            var doc             = app.ActiveUIDocument.Document;
            ElementId?      newId    = null;
            ConnectorProxy? fitConn2 = null;
            string          msg      = string.Empty;

            try
            {
                _txService.RunInTransaction("PipeConnect — Вставка фитинга", txDoc =>
                {
                    (newId, fitConn2, msg) = DoInsertFittingCore(txDoc, defaultFitting);
                });

                if (newId is not null)
                {
                    SizeFittingConnectors(doc, newId, fitConn2);
                    fitConn2 = RealignAfterSizing(doc, newId) ?? fitConn2;
                }
            }
            catch (Exception ex)
            {
                msg = $"Ошибка вставки: {ex.Message}";
            }

            _currentFittingId   = newId;
            _activeFittingConn2 = fitConn2;
            app.ActiveUIDocument?.RefreshActiveView();

            var conns = GetFreeConnectorsSnapshot(doc);
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsBusy        = false;
                StatusMessage = msg;
                InitConnectorCycle(conns);
            });
        });
    }

    // ── Rotation ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RotateLeft()  => ExecuteRotate(-RotationAngleDeg);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RotateRight() => ExecuteRotate(+RotationAngleDeg);

    private void ExecuteRotate(int angleDeg)
    {
        IsBusy = true;
        _eventHandler.Raise(app =>
        {
            try
            {
                _txService.RunInTransaction("PipeConnect — Поворот", doc =>
                {
                    var dynId      = _ctx.DynamicConnector.OwnerElementId;
                    var axisOrigin = _ctx.StaticConnector.OriginVec3;
                    var axisDir    = _ctx.StaticConnector.BasisZVec3;
                    var radians    = angleDeg * System.Math.PI / 180.0;

                    // Собрать ID для группового поворота: dynamic + фитинг + трубы/элементы
                    // подключённые к не-активным коннекторам dynamic-элемента
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

                _totalRotationDeg += angleDeg;
                app.ActiveUIDocument?.RefreshActiveView();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsBusy        = false;
                    StatusMessage = $"Повёрнуто на {angleDeg:+#;-#;0}°";
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsBusy        = false;
                    StatusMessage = $"Ошибка поворота: {ex.Message}";
                });
            }
        });
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

        _eventHandler.Raise(app =>
        {
            ConnectorProxy? refreshed = null;

            try
            {
                // Целевой коннектор выравнивания:
                // — если фитинг вставлен → fitConn2 (коннектор фитинга со стороны dynamic)
                // — иначе → статический коннектор напрямую
                var alignTarget = _activeFittingConn2 ?? _ctx.StaticConnector;

                _txService.RunInTransaction("PipeConnect — Смена коннектора", doc =>
                {
                    // Обновить позицию target: snapshot может быть устаревшим после предыдущих поворотов
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
                    refreshed = _connSvc.RefreshConnector(doc, dynId, target.ConnectorIndex);
                    _totalRotationDeg = 0.0;
                });

                _activeDynamic = refreshed ?? target;
                app.ActiveUIDocument?.RefreshActiveView();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    _visitedConnectorIndices.Add(target.ConnectorIndex);
                    IsBusy        = false;
                    StatusMessage = "Коннектор изменён";
                    CycleConnectorCommand.NotifyCanExecuteChanged();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsBusy        = false;
                    StatusMessage = $"Ошибка: {ex.Message}";
                });
            }
        });
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

    // ── Insert fitting (ручная вставка из ComboBox) ───────────────────────────

    [RelayCommand(CanExecute = nameof(CanInsertFitting))]
    private void InsertFitting()
    {
        if (SelectedFitting is null) return;

        IsBusy = true;
        StatusMessage = "Вставка фитинга…";

        var fitting = SelectedFitting;
        _eventHandler.Raise(app =>
        {
            var doc             = app.ActiveUIDocument.Document;
            ElementId?      newId    = null;
            ConnectorProxy? fitConn2 = null;
            string          rMsg     = string.Empty;

            try
            {
                _txService.RunInTransaction("PipeConnect — Вставка фитинга", txDoc =>
                {
                    if (_currentFittingId is not null)
                    {
                        _fittingInsertSvc.DeleteElement(txDoc, _currentFittingId);
                        _currentFittingId   = null;
                        _activeFittingConn2 = null;
                    }

                    if (fitting.IsDirectConnect)
                    {
                        txDoc.Regenerate();
                        rMsg = "Прямое соединение";
                        return;
                    }

                    (newId, fitConn2, rMsg) = DoInsertFittingCore(txDoc, fitting);
                });

                if (newId is not null)
                {
                    SizeFittingConnectors(doc, newId, fitConn2);
                    fitConn2 = RealignAfterSizing(doc, newId) ?? fitConn2;
                }

                _currentFittingId   = newId;
                _activeFittingConn2 = fitConn2;
                app.ActiveUIDocument?.RefreshActiveView();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsBusy        = false;
                    StatusMessage = rMsg;
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsBusy        = false;
                    StatusMessage = $"Ошибка вставки: {ex.Message}";
                });
            }
        });
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void Connect()
    {
        IsBusy = true;
        StatusMessage = "Соединение…";

        _eventHandler.Raise(app =>
        {
            bool ok = false;
            try
            {
                _txService.RunInTransaction("PipeConnect — ConnectTo", doc =>
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

                        ok = fc1 is not null;
                    }
                    else
                    {
                        var dyn = _activeDynamic ?? _ctx.DynamicConnector;
                        var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
                        ok = _connSvc.ConnectTo(doc,
                            _ctx.StaticConnector.OwnerElementId, _ctx.StaticConnector.ConnectorIndex,
                            dynR.OwnerElementId, dynR.ConnectorIndex);
                    }
                    doc.Regenerate();
                });

                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsBusy          = false;
                    IsSessionActive = false;
                    StatusMessage   = ok ? "Соединение выполнено" : "ConnectTo не удалось";
                    RequestClose?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsBusy        = false;
                    StatusMessage = $"Ошибка: {ex.Message}";
                });
            }
        });
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Cancel()
    {
        if (_isClosing) return;
        _isClosing = true;

        if (!IsSessionActive || (_currentFittingId is null && _totalRotationDeg == 0.0))
        {
            RequestClose?.Invoke();
            return;
        }

        IsBusy = true;
        _eventHandler.Raise(app =>
        {
            try
            {
                // Удалить вставленный фитинг
                if (_currentFittingId is not null)
                {
                    _txService.RunInTransaction("PipeConnect — Отмена (фитинг)", doc =>
                    {
                        _fittingInsertSvc.DeleteElement(doc, _currentFittingId);
                        doc.Regenerate();
                    });
                    _currentFittingId = null;
                }

                // Откатить накопленный поворот
                if (_totalRotationDeg != 0.0)
                {
                    _txService.RunInTransaction("PipeConnect — Отмена (поворот)", doc =>
                    {
                        var radians = -_totalRotationDeg * System.Math.PI / 180.0;
                        _transformSvc.RotateElement(doc,
                            _ctx.DynamicConnector.OwnerElementId,
                            _ctx.StaticConnector.OriginVec3,
                            _ctx.StaticConnector.BasisZVec3,
                            radians);
                        doc.Regenerate();
                    });
                    _totalRotationDeg = 0.0;
                }
            }
            catch { /* best-effort cancel */ }

            Application.Current.Dispatcher.Invoke(() =>
            {
                IsBusy          = false;
                IsSessionActive = false;
                RequestClose?.Invoke();
            });
        });
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

    /// <summary>
    /// Вставить фитинг и выровнять. Вызывается внутри RunInTransaction.
    /// Возвращает (вставленный Id, fitConn2 = коннектор фитинга со стороны dynamic, сообщение).
    /// </summary>
    private (ElementId? id, ConnectorProxy? fitConn2, string message) DoInsertFittingCore(
        Document doc, FittingCardItem item)
    {
        var primary = item.PrimaryFitting;
        if (primary is null)
            return (null, null, "Нет данных о семействе фитинга");

        var insertedId = _fittingInsertSvc.InsertFitting(
            doc, primary.FamilyName, primary.SymbolName, _ctx.StaticConnector.Origin);

        if (insertedId is null)
            return (null, null, $"Семейство '{primary.FamilyName}' не найдено в проекте");

        var fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
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
        return (insertedId, fitConn2, $"Вставлен: {item.DisplayName}");
    }

    /// <summary>
    /// Подобрать размеры коннекторов вставленного фитинга.
    ///
    /// Парный режим (type-параметры, IsInstance=false):
    ///   — Выбирает типоразмер фитинга через TrySetFittingTypeForPair:
    ///     приоритет — точное совпадение со static, затем минимальное отклонение по dynamic.
    ///   — Если достигнутый радиус dynamic-стороны фитинга ≠ исходному dynRadius
    ///     → подгоняет dynamic-элемент под размер фитинга.
    ///
    /// Обычный режим (instance-параметры, IsInstance=true):
    ///   — Записывает каждый коннектор фитинга независимо.
    /// </summary>
    private void SizeFittingConnectors(Document doc, ElementId fittingId, ConnectorProxy? fitConn2)
    {
        try
        {
            var allConns = _connSvc.GetAllFreeConnectors(doc, fittingId).ToList();
            if (allConns.Count == 0) return;

            int conn2Idx = fitConn2?.ConnectorIndex ?? -1;
            int conn1Idx = allConns.FirstOrDefault(c => c.ConnectorIndex != conn2Idx)?.ConnectorIndex ?? -1;

            // Фаза 1: анализ зависимостей (вне транзакции, может использовать EditFamily)
            var depsByIdx = new Dictionary<int, IReadOnlyList<ParameterDependency>>();
            foreach (var c in allConns)
                depsByIdx[c.ConnectorIndex] = _paramResolver.GetConnectorRadiusDependencies(doc, fittingId, c.ConnectorIndex);

            // Парный режим: оба коннектора управляются type-параметром (IsInstance=false).
            // Смена ChangeTypeId влияет сразу на оба — перебирать по одному нельзя.
            bool usePairMode = conn1Idx >= 0 && conn2Idx >= 0
                && depsByIdx.TryGetValue(conn1Idx, out var d1) && d1.Count > 0 && !d1[0].IsInstance
                && depsByIdx.TryGetValue(conn2Idx, out var d2) && d2.Count > 0 && !d2[0].IsInstance;

            if (usePairMode)
            {
                double achievedDynRadius = _ctx.DynamicConnector.Radius;

                // Фаза 2: выбрать тип фитинга с приоритетом static-стороны
                _txService.RunInTransaction("PipeConnect — Размер фитинга", txDoc =>
                {
                    var (_, dynR) = _paramResolver.TrySetFittingTypeForPair(
                        txDoc, fittingId,
                        conn1Idx, _ctx.StaticConnector.Radius,
                        conn2Idx, _ctx.DynamicConnector.Radius);
                    achievedDynRadius = dynR;
                    txDoc.Regenerate();
                });

                // Фаза 3: если dynamic-сторона фитинга ≠ исходному dynRadius → подогнать dynamic-элемент
                const double eps = 1e-6;
                if (System.Math.Abs(achievedDynRadius - _ctx.DynamicConnector.Radius) > eps)
                {
                    var dynId      = _ctx.DynamicConnector.OwnerElementId;
                    var dynConnIdx = _ctx.DynamicConnector.ConnectorIndex;
                    _paramResolver.GetConnectorRadiusDependencies(doc, dynId, dynConnIdx);
                    _txService.RunInTransaction("PipeConnect — Подгонка dynamic под фитинг", txDoc =>
                    {
                        _paramResolver.TrySetConnectorRadius(txDoc, dynId, dynConnIdx, achievedDynRadius);
                        txDoc.Regenerate();
                    });
                }
            }
            else
            {
                // Обычный режим: instance-параметры — каждый коннектор независимо
                _txService.RunInTransaction("PipeConnect — Размер фитинга", txDoc =>
                {
                    foreach (var c in allConns)
                    {
                        double targetRadius = c.ConnectorIndex == conn2Idx
                            ? _ctx.DynamicConnector.Radius
                            : _ctx.StaticConnector.Radius;
                        _paramResolver.TrySetConnectorRadius(txDoc, fittingId, c.ConnectorIndex, targetRadius);
                    }
                    txDoc.Regenerate();
                });
            }
        }
        catch
        {
            // best-effort: fitting connector sizing may not work for all family types
        }
    }

    /// <summary>
    /// После смены типоразмера фитинга его коннекторы смещаются.
    /// Повторно выравниваем фитинг к статическому коннектору и
    /// подтягиваем dynamic-элемент к обновлённому fitConn2.
    /// Возвращает обновлённый fitConn2 (или null при ошибке).
    /// </summary>
    private ConnectorProxy? RealignAfterSizing(Document doc, ElementId fittingId)
    {
        ConnectorProxy? newFitConn2 = null;
        try
        {
            _txService.RunInTransaction("PipeConnect — Выравнивание после размера", txDoc =>
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
        catch
        {
            // best-effort
        }
        return newFitConn2;
    }

    private List<ConnectorProxy> GetFreeConnectorsSnapshot(Document doc)
    {
        try
        {
            return _connSvc.GetAllFreeConnectors(doc, _ctx.DynamicConnector.OwnerElementId).ToList();
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
