using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
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
        {
            if (!rule.IsDirectConnect && rule.FittingFamilies.Count > 0)
            {
                foreach (var family in rule.FittingFamilies.OrderBy(f => f.Priority))
                {
                    AvailableFittings.Add(new FittingCardItem(rule, family));
                }
            }
        }

        SelectedFitting = AvailableFittings.Count > 0 ? AvailableFittings[0] : null;
    }

    /// <summary>
    /// Вызывается из PipeConnectCommand.Execute() ДО ShowDialog().
    /// Открывает TransactionGroup, отсоединяет dynamic, вставляет фитинг,
    /// подбирает размеры — вся цепочка готова до открытия окна.
    /// </summary>
    public void Init()
    {
        _groupSession = _txService.BeginGroupSession("PipeConnect");
        IsSessionActive = true;

        try
        {
            // Шаг 0: отсоединить dynamic от всех существующих соединений,
            // чтобы при перемещении не тянуть за собой присоединённые элементы.
            _groupSession.RunInTransaction("PipeConnect — Отсоединение", doc =>
            {
                var dynId = _ctx.DynamicConnector.OwnerElementId;
                var allConns = _connSvc.GetAllConnectors(doc, dynId);
                foreach (var c in allConns)
                {
                    // IsFree = false означает что коннектор соединён
                    if (!c.IsFree)
                        _connSvc.DisconnectAllFromConnector(doc, dynId, c.ConnectorIndex);
                }
                doc.Regenerate();
            });

            // S3: выравнивание Dynamic-элемента к Static-коннектору (поворот + перемещение)
            var alignResult = _ctx.AlignResult;
            _groupSession.RunInTransaction("PipeConnect — Выравнивание", doc =>
            {
                var dynId = _ctx.DynamicConnector.OwnerElementId;

                SmartConLogger.Info($"[Align] START dynId={dynId.Value} " +
                    $"origin=({_ctx.DynamicConnector.Origin.X:F4},{_ctx.DynamicConnector.Origin.Y:F4},{_ctx.DynamicConnector.Origin.Z:F4}) " +
                    $"BZ=({_ctx.DynamicConnector.BasisZ.X:F3},{_ctx.DynamicConnector.BasisZ.Y:F3},{_ctx.DynamicConnector.BasisZ.Z:F3})");

                // Шаг 1: перемещение
                if (!VectorUtils.IsZero(alignResult.InitialOffset))
                {
                    SmartConLogger.Info($"[Align] Move offset=({alignResult.InitialOffset.X * 304.8:F2}," +
                        $"{alignResult.InitialOffset.Y * 304.8:F2},{alignResult.InitialOffset.Z * 304.8:F2})mm");
                    _transformSvc.MoveElement(doc, dynId, alignResult.InitialOffset);
                }

                // Шаг 2: поворот BasisZ (антипараллельность)
                if (alignResult.BasisZRotation is { } bzRot)
                {
                    SmartConLogger.Info($"[Align] RotateBasisZ angle={bzRot.AngleRadians * 180 / System.Math.PI:F2}° " +
                        $"axis=({bzRot.Axis.X:F3},{bzRot.Axis.Y:F3},{bzRot.Axis.Z:F3})");
                    _transformSvc.RotateElement(doc, dynId,
                        alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);
                }

                // Шаг 3: снэп BasisX к кратному 15°
                if (alignResult.BasisXSnap is { } bxSnap)
                {
                    SmartConLogger.Info($"[Align] RotateBasisXSnap angle={bxSnap.AngleRadians * 180 / System.Math.PI:F2}° " +
                        $"axis=({bxSnap.Axis.X:F3},{bxSnap.Axis.Y:F3},{bxSnap.Axis.Z:F3})");
                    _transformSvc.RotateElement(doc, dynId,
                        alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);
                }

                doc.Regenerate();

                // Шаг 4 (Баг 2): глобальное выравнивание BasisY → ближайший кратный 15°
                // от глобальной оси Y. Устраняет накопленную ошибку угла поворота
                // при последовательных соединениях.
                // Применяется только к FamilyInstance (у труб BasisY не определён через Transform).
                var dynElemRaw = doc.GetElement(dynId);
                if (dynElemRaw is FamilyInstance fiForSnap)
                {
                    var t = fiForSnap.GetTransform();
                    var elemBasisY = new Vec3(t.BasisY.X, t.BasisY.Y, t.BasisY.Z);

                    // Ось вращения для глобального снэпа = ось Z static-коннектора
                    var staticBZ = _ctx.StaticConnector.BasisZVec3;

                    SmartConLogger.Info($"[Align] GlobalYSnap check: elemBasisY=({elemBasisY.X:F3},{elemBasisY.Y:F3},{elemBasisY.Z:F3}) " +
                        $"staticBZ=({staticBZ.X:F3},{staticBZ.Y:F3},{staticBZ.Z:F3})");

                    var globalYSnap = ConnectorAligner.ComputeGlobalYAlignmentSnap(
                        staticBZ, elemBasisY, alignResult.RotationCenter);

                    if (globalYSnap is not null)
                    {
                        SmartConLogger.Info($"[Align] GlobalYSnap APPLY angle={globalYSnap.AngleRadians * 180 / System.Math.PI:F2}°");
                        _transformSvc.RotateElement(doc, dynId,
                            alignResult.RotationCenter, globalYSnap.Axis, globalYSnap.AngleRadians);
                        doc.Regenerate();

                        // Диагностика после снэпа
                        var tAfter = fiForSnap.GetTransform();
                        var byAngle = System.Math.Atan2(tAfter.BasisY.Y, tAfter.BasisY.X) * 180.0 / System.Math.PI;
                        SmartConLogger.Info($"[Align] GlobalYSnap DONE: BasisY ugol v XY={byAngle:F2}°");
                    }
                    else
                    {
                        SmartConLogger.Info("[Align] GlobalYSnap: пропущен (BasisZ ∥ Y или delta≈0)");
                    }
                }

                // Шаг 5: коррекция позиции — после поворотов Origin мог сместиться
                var refreshed = _connSvc.RefreshConnector(
                    doc, dynId, _ctx.DynamicConnector.ConnectorIndex);
                if (refreshed is not null)
                {
                    var correction = _ctx.StaticConnector.OriginVec3 - refreshed.OriginVec3;
                    if (!VectorUtils.IsZero(correction))
                    {
                        SmartConLogger.Info($"[Align] PositionCorrection dist={VectorUtils.Length(correction) * 304.8:F3}mm");
                        _transformSvc.MoveElement(doc, dynId, correction);
                    }
                }

                doc.Regenerate();

                // Итоговое состояние для диагностики
                var refreshedFinal = _connSvc.RefreshConnector(doc, dynId, _ctx.DynamicConnector.ConnectorIndex);
                if (refreshedFinal is not null)
                {
                    var distToStatic = VectorUtils.DistanceTo(refreshedFinal.OriginVec3, _ctx.StaticConnector.OriginVec3);
                    SmartConLogger.Info($"[Align] END: dynOrigin=({refreshedFinal.Origin.X:F4},{refreshedFinal.Origin.Y:F4},{refreshedFinal.Origin.Z:F4}) " +
                        $"distToStatic={distToStatic * 304.8:F3}mm");
                }
            });

            _activeDynamic = _connSvc.RefreshConnector(
                _doc, _ctx.DynamicConnector.OwnerElementId, _ctx.DynamicConnector.ConnectorIndex)
                ?? _ctx.DynamicConnector;

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
            else if (_ctx.ParamTargetRadius is { } directTargetRadius)
            {
                // Прямое соединение без фитинга — подгоняем dynamic под static
                _groupSession.RunInTransaction("PipeConnect — Подгонка размера", doc =>
                {
                    var dynId = _ctx.DynamicConnector.OwnerElementId;
                    var dynIdx = _ctx.DynamicConnector.ConnectorIndex;

                    _paramResolver.TrySetConnectorRadius(doc, dynId, dynIdx, directTargetRadius);
                    doc.Regenerate();

                    // Баг 1: после смены размера семейство может сместиться (Revit переоценивает
                    // геометрию). Корректируем позицию коннектора обратно к static-коннектору.
                    var refreshedAfterSize = _connSvc.RefreshConnector(doc, dynId, dynIdx);
                    if (refreshedAfterSize is not null)
                    {
                        var posCorrection = _ctx.StaticConnector.OriginVec3 - refreshedAfterSize.OriginVec3;
                        if (!VectorUtils.IsZero(posCorrection))
                        {
                            SmartConLogger.Info($"[SizeAdj] Коррекция позиции после смены размера: " +
                                $"dist={VectorUtils.Length(posCorrection) * 304.8:F3}mm");
                            _transformSvc.MoveElement(doc, dynId, posCorrection);
                            doc.Regenerate();
                        }
                    }
                });
                _activeDynamic = _connSvc.RefreshConnector(
                    _doc, _ctx.DynamicConnector.OwnerElementId, _ctx.DynamicConnector.ConnectorIndex)
                    ?? _ctx.DynamicConnector;
                StatusMessage = "Готово к соединению";
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
    private void RotateLeft()  => ExecuteRotate(+RotationAngleDeg);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RotateRight() => ExecuteRotate(-RotationAngleDeg);

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
            // Прогреваем кеш для нового коннектора (может потребовать EditFamily)
            _paramResolver.GetConnectorRadiusDependencies(_doc, target.OwnerElementId, target.ConnectorIndex);

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
        var newFitConn2 = SizeFittingConnectors(_doc, insertedId, fitConn2);
        if (newFitConn2 is not null)
            _activeFittingConn2 = newFitConn2;
    }

    // ── Connect ───────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void Connect()
    {
        IsBusy = true;
        StatusMessage = "Проверка и финальная подгонка…";

        try
        {
            // Финальная валидация и корректировка перед соединением
            ValidateAndFixBeforeConnect();

            // ── Диагностика: позиции ДО ConnectTo ──
            LogConnectorState("ДО ConnectTo");

            _groupSession!.RunInTransaction("PipeConnect — ConnectTo", doc =>
            {
                doc.Regenerate();

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

                // ── Диагностика: позиции ПОСЛЕ ConnectTo ──
                LogConnectorState("ПОСЛЕ ConnectTo");
            });

            _groupSession!.Assimilate();
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

    /// <summary>
    /// Финальная валидация и автокорректировка перед ConnectTo.
    /// Проверяет каждую пару соединяемых коннекторов:
    ///   - Совпадение радиусов (± eps). При несовпадении — пытается подогнать dynamic под fitting.
    ///   - Совпадение позиций (± 0.1 мм). При смещении — корректирует MoveElement.
    ///   - Антипараллельность BasisZ (предупреждение в лог, не блокирует).
    /// Все исправления — внутри RunInTransaction.
    /// </summary>
    private void ValidateAndFixBeforeConnect()
    {
        const double radiusEps     = 1e-5;   // ~0.003 мм — физически незначимо
        const double positionEpsM  = 0.0001; // 0.1 мм в метрах
        const double positionEpsFt = positionEpsM / 0.3048;
        const double angleEpsDeg   = 1.0;    // 1 градус

        SmartConLogger.LookupSection("ValidateAndFixBeforeConnect");

        _groupSession!.RunInTransaction("PipeConnect — Финальная корректировка", doc =>
        {
            var dyn    = _activeDynamic ?? _ctx.DynamicConnector;
            var dynFresh = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;

            // ── Пара static ↔ fitting.conn1 или static ↔ dynamic (при прямом соединении) ──
            if (_currentFittingId is not null)
            {
                var fConns = _connSvc.GetAllFreeConnectors(doc, _currentFittingId).ToList();

                // Conn1 фитинга (ближайший к static)
                var fc1 = fConns
                    .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, _ctx.StaticConnector.OriginVec3))
                    .FirstOrDefault();

                // Conn2 фитинга (дальний = dynamic-сторона)
                var fc2 = fConns.FirstOrDefault(c => c.ConnectorIndex != (fc1?.ConnectorIndex ?? -1));

                if (fc1 is not null)
                {
                    // Позиция: фитинг должен быть прямо у static
                    var posErr1 = VectorUtils.DistanceTo(fc1.OriginVec3, _ctx.StaticConnector.OriginVec3);
                    if (posErr1 > positionEpsFt)
                    {
                        SmartConLogger.Warn($"[Validate] fc1 смещён от static на {posErr1 * 304.8:F2} мм — корректируем");
                        var correction = _ctx.StaticConnector.OriginVec3 - fc1.OriginVec3;
                        _transformSvc.MoveElement(doc, _currentFittingId, correction);
                        doc.Regenerate();
                        fc1 = _connSvc.RefreshConnector(doc, _currentFittingId, fc1.ConnectorIndex) ?? fc1;
                        fc2 = fc2 is not null
                            ? _connSvc.RefreshConnector(doc, _currentFittingId, fc2.ConnectorIndex) ?? fc2
                            : null;
                    }

                    // Размер fc1 должен совпадать со static
                    double r1Err = System.Math.Abs(fc1.Radius - _ctx.StaticConnector.Radius);
                    SmartConLogger.Lookup($"  fc1 R={fc1.Radius * 304.8:F2}mm, static R={_ctx.StaticConnector.Radius * 304.8:F2}mm, Δ={r1Err * 304.8:F2}mm");
                    if (r1Err > radiusEps)
                        SmartConLogger.Warn($"[Validate] НЕСОВПАДЕНИЕ: fc1.Radius≠static.Radius (Δ={r1Err * 304.8:F2}мм). Фитинг подобран неоптимально.");
                }

                if (fc2 is not null)
                {
                    // Размер fc2 должен совпадать с dynamic; если нет — пытаемся подогнать dynamic
                    double r2Err = System.Math.Abs(fc2.Radius - dynFresh.Radius);
                    SmartConLogger.Lookup($"  fc2 R={fc2.Radius * 304.8:F2}mm, dyn R={dynFresh.Radius * 304.8:F2}mm, Δ={r2Err * 304.8:F2}mm");
                    if (r2Err > radiusEps)
                    {
                        SmartConLogger.Warn($"[Validate] Несовпадение fc2↔dynamic Δ={r2Err * 304.8:F2}мм — пытаемся подогнать dynamic");
                        bool fixed1 = _paramResolver.TrySetConnectorRadius(
                            doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, fc2.Radius);
                        doc.Regenerate();
                        if (fixed1)
                        {
                            dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                            _activeDynamic = dynFresh;
                            SmartConLogger.Lookup($"  → dynamic подогнан до {dynFresh.Radius * 304.8:F2}mm");
                        }
                        else
                        {
                            SmartConLogger.Warn($"[Validate] Подгонка dynamic не удалась — несовпадение размеров в модели сохраняется");
                        }
                    }

                    // Позиция: dynamic должен быть у fc2
                    var posErr2 = VectorUtils.DistanceTo(dynFresh.OriginVec3, fc2.OriginVec3);
                    SmartConLogger.Lookup($"  позиция: dyn↔fc2 расстояние={posErr2 * 304.8:F2}mm");
                    if (posErr2 > positionEpsFt)
                    {
                        SmartConLogger.Warn($"[Validate] dynamic смещён от fc2 на {posErr2 * 304.8:F2} мм — корректируем");
                        var offset = fc2.OriginVec3 - dynFresh.OriginVec3;
                        _transformSvc.MoveElement(doc, dynFresh.OwnerElementId, offset);
                        doc.Regenerate();
                    }

                    // BasisZ: fc2 и dynFresh должны быть антипараллельны
                    double angleZ = VectorUtils.AngleBetween(fc2.BasisZVec3, dynFresh.BasisZVec3);
                    double antiParallelErr = System.Math.Abs(angleZ - System.Math.PI) * 180.0 / System.Math.PI;
                    SmartConLogger.Lookup($"  BasisZ: угол fc2↔dyn={angleZ * 180 / System.Math.PI:F1}° (идеал=180°, отклон={antiParallelErr:F1}°)");
                    if (antiParallelErr > angleEpsDeg)
                        SmartConLogger.Warn($"[Validate] ПРЕДУПРЕЖДЕНИЕ: BasisZ не антипараллельны (откл. {antiParallelErr:F1}°) — коннект может не пройти");
                }
            }
            else
            {
                // Прямое соединение: static ↔ dynamic
                double rErr = System.Math.Abs(_ctx.StaticConnector.Radius - dynFresh.Radius);
                SmartConLogger.Lookup($"  прямое: static R={_ctx.StaticConnector.Radius * 304.8:F2}mm, dyn R={dynFresh.Radius * 304.8:F2}mm, Δ={rErr * 304.8:F2}mm");
                if (rErr > radiusEps)
                {
                    SmartConLogger.Warn($"[Validate] Прямое: несовпадение Δ={rErr * 304.8:F2}мм — пытаемся подогнать dynamic");
                    bool fixed2 = _paramResolver.TrySetConnectorRadius(
                        doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, _ctx.StaticConnector.Radius);
                    doc.Regenerate();
                    if (fixed2)
                    {
                        dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                        _activeDynamic = dynFresh;
                    }
                }

                // Позиция
                var posErrD = VectorUtils.DistanceTo(dynFresh.OriginVec3, _ctx.StaticConnector.OriginVec3);
                if (posErrD > positionEpsFt)
                {
                    var offset = _ctx.StaticConnector.OriginVec3 - dynFresh.OriginVec3;
                    _transformSvc.MoveElement(doc, dynFresh.OwnerElementId, offset);
                    doc.Regenerate();
                }

                // BasisZ
                double angleZD = VectorUtils.AngleBetween(_ctx.StaticConnector.BasisZVec3, dynFresh.BasisZVec3);
                double antiErrD = System.Math.Abs(angleZD - System.Math.PI) * 180.0 / System.Math.PI;
                if (antiErrD > angleEpsDeg)
                    SmartConLogger.Warn($"[Validate] ПРЕДУПРЕЖДЕНИЕ: BasisZ не антипараллельны (откл. {antiErrD:F1}°)");
            }

            doc.Regenerate();
        });
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

    private ConnectorProxy? SizeFittingConnectors(Document doc, ElementId fittingId, ConnectorProxy? fitConn2)
    {
        ConnectorProxy? result = null;
        try
        {
            var allConns = _connSvc.GetAllFreeConnectors(doc, fittingId).ToList();
            if (allConns.Count == 0) return null;

            // Определяем conn1 (сторона static) и conn2 (сторона dynamic):
            // 1) Если fitConn2 явно задан и его индекс есть в списке — доверяем ему.
            // 2) Иначе — геометрический fallback: коннектор ближе к static = conn1, дальний = conn2.
            ConnectorProxy? resolvedConn2;
            ConnectorProxy? resolvedConn1;
            if (fitConn2 is not null && allConns.Any(c => c.ConnectorIndex == fitConn2.ConnectorIndex))
            {
                resolvedConn2 = allConns.First(c => c.ConnectorIndex == fitConn2.ConnectorIndex);
                resolvedConn1 = allConns.FirstOrDefault(c => c.ConnectorIndex != fitConn2.ConnectorIndex);
            }
            else
            {
                // Геометрический fallback: ближайший к static = conn1
                var ordered = allConns
                    .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, _ctx.StaticConnector.OriginVec3))
                    .ToList();
                resolvedConn1 = ordered.FirstOrDefault();
                resolvedConn2 = ordered.Skip(1).FirstOrDefault();
            }

            int conn1Idx = resolvedConn1?.ConnectorIndex ?? -1;
            int conn2Idx = resolvedConn2?.ConnectorIndex ?? -1;

            var depsByIdx = new Dictionary<int, IReadOnlyList<ParameterDependency>>();
            foreach (var c in allConns)
                depsByIdx[c.ConnectorIndex] = _paramResolver.GetConnectorRadiusDependencies(doc, fittingId, c.ConnectorIndex);

            bool usePairMode = conn1Idx >= 0 && conn2Idx >= 0
                && depsByIdx.TryGetValue(conn1Idx, out var d1) && d1.Count > 0 && !d1[0].IsInstance
                && depsByIdx.TryGetValue(conn2Idx, out var d2) && d2.Count > 0 && !d2[0].IsInstance;

            if (usePairMode)
            {
                // Используем ИСХОДНЫЙ радиус dynamic (до S4) для подбора фитинга.
                // Алгоритм: фитинг подбирается к static точно, а к dynamic — максимально близко
                // к его оригинальному размеру. После этого dynamic подгоняется под достигнутый
                // радиус фитинга (а не наоборот).
                double originalDynRadius = _ctx.DynamicConnector.Radius;
                double achievedDynRadius = originalDynRadius;

                _groupSession!.RunInTransaction("PipeConnect — Размер фитинга", txDoc =>
                {
                    var (_, dynR) = _paramResolver.TrySetFittingTypeForPair(
                        txDoc, fittingId,
                        conn1Idx, _ctx.StaticConnector.Radius,
                        conn2Idx, originalDynRadius);
                    achievedDynRadius = dynR;
                    txDoc.Regenerate();
                });

                // Подгоняем dynamic под размер dynamic-коннектора фитинга
                const double eps = 1e-6;
                var dynId      = _ctx.DynamicConnector.OwnerElementId;
                var dynConnIdx = _ctx.DynamicConnector.ConnectorIndex;
                double currentDynRadius = _activeDynamic?.Radius ?? originalDynRadius;
                if (System.Math.Abs(achievedDynRadius - currentDynRadius) > eps)
                {
                    _groupSession.RunInTransaction("PipeConnect — Подгонка dynamic под фитинг", txDoc =>
                    {
                        _paramResolver.TrySetConnectorRadius(txDoc, dynId, dynConnIdx, achievedDynRadius);
                        txDoc.Regenerate();
                    });
                }
            }
            else
            {
                // Для фитингов с независимыми параметрами коннекторов:
                // conn1 (к static) → staticRadius, conn2 (к dynamic) → исходный радиус dynamic
                double originalDynRadius = _ctx.DynamicConnector.Radius;

                _groupSession!.RunInTransaction("PipeConnect — Размер фитинга", txDoc =>
                {
                    foreach (var c in allConns)
                    {
                        double targetRadius = c.ConnectorIndex == conn2Idx
                            ? originalDynRadius
                            : _ctx.StaticConnector.Radius;
                        _paramResolver.TrySetConnectorRadius(txDoc, fittingId, c.ConnectorIndex, targetRadius);
                    }
                    txDoc.Regenerate();
                });
            }

            result = RealignAfterSizing(doc, fittingId);
        }
        catch { /* best-effort */ }
        return result;
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

    /// <summary>
    /// Диагностика: логирует позиции, радиусы и углы осей всех коннекторов.
    /// Вызывается ДО и ПОСЛЕ ConnectTo для выявления смещений.
    /// </summary>
    private void LogConnectorState(string label)
    {
        try
        {
            var dyn = _activeDynamic ?? _ctx.DynamicConnector;
            var dynR = _connSvc.RefreshConnector(_doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;

            SmartConLogger.LookupSection($"[DIAG {label}]");

            // Static
            var st = _ctx.StaticConnector;
            SmartConLogger.Lookup($"  static: origin=({st.Origin.X:F4},{st.Origin.Y:F4},{st.Origin.Z:F4}) R={st.Radius * 304.8:F2}mm Z=({st.BasisZ.X:F3},{st.BasisZ.Y:F3},{st.BasisZ.Z:F3})");

            if (_currentFittingId is not null)
            {
                // Fitting
                var fConns = _connSvc.GetAllFreeConnectors(_doc, _currentFittingId).ToList();
                foreach (var fc in fConns)
                {
                    var angleToStatic = VectorUtils.AngleBetween(fc.BasisZVec3, st.BasisZVec3) * 180.0 / System.Math.PI;
                    var angleToDyn = VectorUtils.AngleBetween(fc.BasisZVec3, dynR.BasisZVec3) * 180.0 / System.Math.PI;
                    var distToStatic = VectorUtils.DistanceTo(fc.OriginVec3, st.OriginVec3) * 304.8;
                    var distToDyn = VectorUtils.DistanceTo(fc.OriginVec3, dynR.OriginVec3) * 304.8;
                    SmartConLogger.Lookup($"  fitting conn#{fc.ConnectorIndex}: origin=({fc.Origin.X:F4},{fc.Origin.Y:F4},{fc.Origin.Z:F4}) R={fc.Radius * 304.8:F2}mm Z=({fc.BasisZ.X:F3},{fc.BasisZ.Y:F3},{fc.BasisZ.Z:F3})");
                    SmartConLogger.Lookup($"    dist→static={distToStatic:F3}mm  dist→dyn={distToDyn:F3}mm  ∠→static={angleToStatic:F1}°  ∠→dyn={angleToDyn:F1}°");
                }
            }

            // Dynamic
            var angleZ = VectorUtils.AngleBetween(st.BasisZVec3, dynR.BasisZVec3) * 180.0 / System.Math.PI;
            var distSD = VectorUtils.DistanceTo(st.OriginVec3, dynR.OriginVec3) * 304.8;
            SmartConLogger.Lookup($"  dynamic: origin=({dynR.Origin.X:F4},{dynR.Origin.Y:F4},{dynR.Origin.Z:F4}) R={dynR.Radius * 304.8:F2}mm Z=({dynR.BasisZ.X:F3},{dynR.BasisZ.Y:F3},{dynR.BasisZ.Z:F3})");
            SmartConLogger.Lookup($"    dist→static={distSD:F3}mm  ∠Z(static↔dyn)={angleZ:F1}°");

            // Transform элемента dynamic
            var dynElem = _doc.GetElement(dynR.OwnerElementId);
            if (dynElem is FamilyInstance fi)
            {
                var t = fi.GetTransform();
                var basisXAngle = System.Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180.0 / System.Math.PI;
                var basisYAngle = System.Math.Atan2(t.BasisY.Y, t.BasisY.X) * 180.0 / System.Math.PI;
                SmartConLogger.Lookup($"  dynamic Transform: origin=({t.Origin.X:F4},{t.Origin.Y:F4},{t.Origin.Z:F4}) BX=({t.BasisX.X:F3},{t.BasisX.Y:F3},{t.BasisX.Z:F3}) BY=({t.BasisY.X:F3},{t.BasisY.Y:F3},{t.BasisY.Z:F3}) BZ=({t.BasisZ.X:F3},{t.BasisZ.Y:F3},{t.BasisZ.Z:F3})");
                SmartConLogger.Lookup($"  dynamic BasisX угол в XY={basisXAngle:F2}°  BasisY угол в XY={basisYAngle:F2}°");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[DIAG {label}] Ошибка логирования: {ex.Message}");
        }
    }
}
