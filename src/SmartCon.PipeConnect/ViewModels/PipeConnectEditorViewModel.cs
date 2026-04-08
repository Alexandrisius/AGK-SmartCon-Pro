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
    private readonly IDynamicSizeResolver     _sizeResolver;
    private readonly INetworkMover            _networkMover;
    private readonly IFittingMappingRepository _mappingRepo;
    private readonly IDialogService           _dialogSvc;
    private readonly PipeConnectSessionContext _ctx;

    private ITransactionGroupSession? _groupSession;
    private ElementId?      _currentFittingId;
    private ConnectorProxy? _activeDynamic;
    private ConnectorProxy? _activeFittingConn2;
    private FittingMappingRule? _activeFittingRule;
    private bool            _isClosing;

    private List<ConnectorProxy>  _allDynamicConnectors = [];
    private readonly HashSet<int> _visitedConnectorIndices = [];
    private int                   _connectorCyclePos = 0;

    // ── Chain ───────────────────────────────────────────────
    private ConnectionGraph? _chainGraph;
    private readonly NetworkSnapshotStore _snapshotStore = new();

    private int    _chainDepthField;
    [ObservableProperty] private string _chainDepthHint = "нет цепочки";
    [ObservableProperty] private bool   _hasChain;

    public int ChainDepth
    {
        get => _chainDepthField;
        set => SetProperty(ref _chainDepthField, value);
    }

    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string  _statusMessage = "Инициализация…";
    [ObservableProperty] private FittingCardItem? _selectedFitting;
    [ObservableProperty] private bool    _isSessionActive;

    public ObservableCollection<FittingCardItem> AvailableFittings { get; } = [];

    [ObservableProperty] private int _rotationAngleDeg = 15;
    [ObservableProperty] private SizeOption? _selectedDynamicSize;
    [ObservableProperty] private bool _hasSizeOptions;

    public ObservableCollection<SizeOption> AvailableDynamicSizes { get; } = [];

    public event Action? RequestClose;

    public PipeConnectEditorViewModel(
        PipeConnectSessionContext  ctx,
        Document                   doc,
        ITransactionService        txService,
        IConnectorService          connSvc,
        ITransformService          transformSvc,
        IFittingInsertService      fittingInsertSvc,
        IParameterResolver         paramResolver,
        IDynamicSizeResolver       sizeResolver,
        INetworkMover              networkMover,
        IFittingMappingRepository  mappingRepo,
        IDialogService             dialogSvc)
    {
        _ctx              = ctx;
        _doc              = doc;
        _txService        = txService;
        _connSvc          = connSvc;
        _transformSvc     = transformSvc;
        _fittingInsertSvc = fittingInsertSvc;
        _paramResolver    = paramResolver;
        _sizeResolver     = sizeResolver;
        _networkMover     = networkMover;
        _mappingRepo      = mappingRepo;
        _dialogSvc        = dialogSvc;
        _activeDynamic    = ctx.DynamicConnector;
        _chainGraph       = ctx.ChainGraph;

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

            if (rule.FromType.Value == rule.ToType.Value && rule.ReducerFamilies.Count > 0)
            {
                foreach (var reducer in rule.ReducerFamilies.OrderBy(f => f.Priority))
                {
                    AvailableFittings.Add(new FittingCardItem(rule, reducer, isReducer: true));
                }
            }
        }

        SelectedFitting = AvailableFittings.Count > 0 ? AvailableFittings[0] : null;

        LoadDynamicSizes();
        UpdateChainUI();
    }

    private void LoadDynamicSizes()
    {
        SmartConLogger.LookupSection("PipeConnectEditorViewModel.LoadDynamicSizes");

        try
        {
            var dynId = _ctx.DynamicConnector.OwnerElementId;
            var dynIdx = _ctx.DynamicConnector.ConnectorIndex;
            SmartConLogger.Lookup($"  elementId={dynId.Value}, connIdx={dynIdx}");

            SmartConLogger.Info($"[MultiCol] LoadDynamicSizes: constraints={_ctx.LookupConstraints.Count} для dynId={dynId.Value}, connIdx={dynIdx}");
            var sizes = _sizeResolver.GetAvailableSizes(_doc, dynId, dynIdx, _ctx.LookupConstraints);
            SmartConLogger.Lookup($"  SizeResolver вернул {sizes.Count} размеров");
            SmartConLogger.Info($"[MultiCol] LoadDynamicSizes: SizeResolver вернул {sizes.Count} размеров");

            var currentRadius = _ctx.DynamicConnector.Radius;
            var currentDn = (int)Math.Round(currentRadius * 2.0 * 304.8);
            SmartConLogger.Lookup($"  Текущий размер: DN {currentDn} (radius={currentRadius * 304.8:F2} мм)");

            AvailableDynamicSizes.Add(new SizeOption
            {
                DisplayName = $"АВТОПОДБОР (DN {currentDn})",
                Radius = currentRadius,
                Source = "",
                IsAutoSelect = true
            });

            foreach (var size in sizes)
            {
                var dn = (int)Math.Round(size.Radius * 2.0 * 304.8);
                var label = $"DN {dn}";
                if (!AvailableDynamicSizes.Any(s => s.DisplayName == label))
                {
                    AvailableDynamicSizes.Add(new SizeOption
                    {
                        DisplayName = label,
                        Radius = size.Radius,
                        Source = size.Source,
                        IsAutoSelect = false
                    });
                    SmartConLogger.Lookup($"  Добавлен размер: {label} (source={size.Source})");
                }
            }

            SelectedDynamicSize = AvailableDynamicSizes.Count > 0 ? AvailableDynamicSizes[0] : null;
            HasSizeOptions = AvailableDynamicSizes.Count > 1;
            SmartConLogger.Lookup($"  Итого: {AvailableDynamicSizes.Count} опций, HasSizeOptions={HasSizeOptions}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[LoadDynamicSizes] Ошибка: {ex.Message}");
            HasSizeOptions = false;
        }
    }

    /// <summary>
    /// Обновить пункт "АВТОПОДБОР" в списке размеров после Init() или смены размера.
    /// </summary>
    private void RefreshAutoSelectSize()
    {
        if (_activeDynamic is null) return;

        var actualDn = (int)Math.Round(_activeDynamic.Radius * 2.0 * 304.8);
        var autoLabel = $"АВТОПОДБОР (DN {actualDn})";

        // Обновить первый элемент (АВТОПОДБОР)
        if (AvailableDynamicSizes.Count > 0)
        {
            var autoOption = new SizeOption
            {
                DisplayName = autoLabel,
                Radius = _activeDynamic.Radius,
                Source = "",
                IsAutoSelect = true
            };
            AvailableDynamicSizes[0] = autoOption;
            SmartConLogger.Lookup($"[RefreshAutoSelectSize] Обновлено: {autoLabel}");
        }

        // Если текущий выбранный — АВТОПОДБОР, обновить его
        if (SelectedDynamicSize?.IsAutoSelect == true)
        {
            SelectedDynamicSize = AvailableDynamicSizes[0];
        }
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

            // Обновить "АВТОПОДБОР" в списке размеров — после Init() размер мог измениться
            RefreshAutoSelectSize();
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

                // Элементы цепочки до ChainDepth + reducer-ы
                if (_chainGraph is not null && ChainDepth > 0)
                {
                    for (int level = 1; level <= ChainDepth && level < _chainGraph.Levels.Count; level++)
                    {
                        foreach (var elemId in _chainGraph.Levels[level])
                        {
                            idsToRotate.Add(elemId);
                            foreach (var reducerId in _snapshotStore.GetReducers(elemId))
                                idsToRotate.Add(reducerId);
                        }
                    }
                }

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

                // GlobalYSnap ТОЛЬКО для dynamic (элементы цепочки — rigid body)
                var dynElemForSnap = doc.GetElement(dynId);
                if (dynElemForSnap is FamilyInstance fiForSnap)
                {
                    var t = fiForSnap.GetTransform();
                    var elemBasisY = new Vec3(t.BasisY.X, t.BasisY.Y, t.BasisY.Z);
                    var staticBZ = _ctx.StaticConnector.BasisZVec3;
                    var globalYSnap = ConnectorAligner.ComputeGlobalYAlignmentSnap(
                        staticBZ, elemBasisY, axisOrigin);
                    if (globalYSnap is not null)
                    {
                        _transformSvc.RotateElement(doc, dynId,
                            axisOrigin, globalYSnap.Axis, globalYSnap.AngleRadians);
                    }
                }

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

    // ── Change dynamic size ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanChangeDynamicSize))]
    private void ChangeDynamicSize()
    {
        if (SelectedDynamicSize is null || SelectedDynamicSize.IsAutoSelect) return;

        IsBusy = true;
        var targetDn = (int)Math.Round(SelectedDynamicSize.Radius * 2.0 * 304.8);
        StatusMessage = $"Изменение размера на DN {targetDn}…";
        SmartConLogger.Info($"[ChangeDynamicSize] Попытка смены размера на DN {targetDn} (radius={SelectedDynamicSize.Radius * 304.8:F2} мм, source={SelectedDynamicSize.Source})");

        try
        {
            var dynId = _ctx.DynamicConnector.OwnerElementId;
            var dynIdx = _ctx.DynamicConnector.ConnectorIndex;
            var targetRadius = SelectedDynamicSize.Radius;

            _groupSession!.RunInTransaction("PipeConnect — Смена размера dynamic", doc =>
            {
                bool success = _paramResolver.TrySetConnectorRadius(doc, dynId, dynIdx, targetRadius);
                SmartConLogger.Info($"[ChangeDynamicSize] TrySetConnectorRadius: {(success ? "OK" : "FAILED")}");
                doc.Regenerate();

                var refreshed = _connSvc.RefreshConnector(doc, dynId, dynIdx);
                if (refreshed is not null)
                {
                    var correction = _ctx.StaticConnector.OriginVec3 - refreshed.OriginVec3;
                    if (!VectorUtils.IsZero(correction))
                    {
                        var distMm = VectorUtils.Length(correction) * 304.8;
                        SmartConLogger.Info($"[ChangeDynamicSize] PositionCorrection: {distMm:F3} мм");
                        _transformSvc.MoveElement(doc, dynId, correction);
                    }
                }

                doc.Regenerate();
                _activeDynamic = _connSvc.RefreshConnector(doc, dynId, dynIdx) ?? _activeDynamic;
                if (_activeDynamic is not null)
                {
                    var actualDn = (int)Math.Round(_activeDynamic.Radius * 2.0 * 304.8);
                    SmartConLogger.Info($"[ChangeDynamicSize] Фактический размер после смены: DN {actualDn}");
                }
            });

            StatusMessage = $"Размер изменён на DN {targetDn}";

            if (_currentFittingId is not null)
            {
                StatusMessage = "Обновление фитинга…";
                var currentFitting = SelectedFitting;
                if (currentFitting is not null && !currentFitting.IsDirectConnect)
                {
                    SmartConLogger.Info($"[ChangeDynamicSize] Автообновление фитинга: {currentFitting.DisplayName}");
                    InsertFittingSilentNoDynamicAdjust(currentFitting);
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[ChangeDynamicSize] Ошибка: {ex.Message}");
            StatusMessage = $"Ошибка смены размера: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanChangeDynamicSize() =>
        IsSessionActive && !IsBusy &&
        SelectedDynamicSize is not null && !SelectedDynamicSize.IsAutoSelect;

    partial void OnSelectedDynamicSizeChanged(SizeOption? value)
    {
        ChangeDynamicSizeCommand.NotifyCanExecuteChanged();
    }

    // ── Chain depth (+/−) ─────────────────────────────────────────────────────

    private const int MaxChainLevel = 30;

    [RelayCommand(CanExecute = nameof(CanIncrementChain))]
    private void IncrementChainDepth()
    {
        if (_chainGraph is null) return;
        var graph = _chainGraph;
        int nextLevel = ChainDepth + 1;
        if (nextLevel >= graph.Levels.Count) return;
        var levelElements = graph.Levels[nextLevel];

        IsBusy = true;
        StatusMessage = $"Присоединение уровня {nextLevel}…";

        try
        {
            SmartConLogger.Info($"[Chain+] ═══ УРОВЕНЬ {nextLevel} ═══ ({levelElements.Count} элементов)");

            // Захватить snapshot КАЖДОГО элемента уровня ДО модификации
            foreach (var elemId in levelElements)
            {
                var snapshot = CaptureSnapshot(_doc, elemId, graph);
                _snapshotStore.Save(snapshot);
                SmartConLogger.Info($"[Chain+] Snapshot: elemId={elemId.Value}, " +
                    $"isMepCurve={snapshot.IsMepCurve}, " +
                    $"R={snapshot.ConnectorRadius * 304.8:F2}mm (DN{System.Math.Round(snapshot.ConnectorRadius * 2.0 * 304.8)}), " +
                    $"symbolId={snapshot.FamilySymbolId?.Value}, connections={snapshot.Connections.Count}");
            }

            var comparer = ElementIdEqualityComparer.Instance;

            _groupSession!.RunInTransaction($"Цепочка: уровень {nextLevel}", doc =>
            {
                int elemIndex = 0;
                foreach (var elemId in levelElements)
                {
                    elemIndex++;
                    var elemRaw = doc.GetElement(elemId);
                    string elemName = elemRaw?.Name ?? "?";
                    string elemType = elemRaw?.GetType().Name ?? "?";
                    SmartConLogger.Info($"[Chain+] ── Элемент {elemIndex}/{levelElements.Count}: " +
                        $"id={elemId.Value} '{elemName}' ({elemType}) ──");

                    // ── a. Disconnect от ВСЕХ соседей ──
                    var allConns = _connSvc.GetAllConnectors(doc, elemId);
                    int disconnected = 0;
                    foreach (var c in allConns)
                    {
                        if (!c.IsFree)
                        {
                            SmartConLogger.Info($"[Chain+]   a. Disconnect connIdx={c.ConnectorIndex} " +
                                $"(R={c.Radius * 304.8:F2}mm)");
                            _connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex);
                            disconnected++;
                        }
                    }
                    SmartConLogger.Info($"[Chain+]   a. Disconnect done: {disconnected} соединений разорвано, " +
                        $"всего коннекторов={allConns.Count}");

                    // ── b. Найти ребро к родителю (уровень N-1) ──
                    var edge = FindEdgeToParent(elemId, nextLevel, graph);
                    if (edge is null)
                    {
                        SmartConLogger.Warn($"[Chain+]   b. Ребро к родителю НЕ НАЙДЕНО → skip");
                        continue;
                    }
                    SmartConLogger.Info($"[Chain+]   b. Ребро: parent={edge.Value.ParentId.Value} " +
                        $"parentConnIdx={edge.Value.ParentConnIdx}, elemConnIdx={edge.Value.ElemConnIdx}");

                    // Получаем parentProxy для определения targetRadius
                    var parentProxy = _connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
                    if (parentProxy is null)
                    {
                        SmartConLogger.Warn($"[Chain+]   parentProxy=NULL → skip");
                        continue;
                    }
                    SmartConLogger.Info($"[Chain+]   parent: R={parentProxy.Radius * 304.8:F2}mm " +
                        $"(DN{System.Math.Round(parentProxy.Radius * 2.0 * 304.8)}) " +
                        $"origin=({parentProxy.Origin.X:F4},{parentProxy.Origin.Y:F4},{parentProxy.Origin.Z:F4})");

                    // ══════════════════════════════════════════════════════════════
                    // ПОРЯДОК: СНАЧАЛА AdjustSize, ПОТОМ Align
                    // После смены размера (напр. DN65→DN15) геометрия элемента
                    // полностью перестраивается, коннекторы смещаются.
                    // Выравнивание нужно делать ПОСЛЕ смены размера.
                    // ══════════════════════════════════════════════════════════════

                    // ── c. AdjustSize ──
                    // Каскадный подбор: targetRadius = радиус РОДИТЕЛЬСКОГО коннектора
                    ElementId? reducerId = null;
                    double targetRadius = parentProxy.Radius;
                    double targetDn = System.Math.Round(targetRadius * 2.0 * 304.8);

                    {
                        var elemRefreshed = _connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
                        double elemRadius = elemRefreshed?.Radius ?? 0;
                        double elemDn = System.Math.Round(elemRadius * 2.0 * 304.8);
                        double delta = System.Math.Abs(targetRadius - elemRadius);

                        SmartConLogger.Info($"[Chain+]   c. AdjustSize: target={targetRadius * 304.8:F2}mm (DN{targetDn}), " +
                            $"elem={elemRadius * 304.8:F2}mm (DN{elemDn}), delta={delta * 304.8:F4}mm, needsAdjust={delta > 1e-5}");

                        if (elemRefreshed is not null && delta > 1e-5)
                        {
                            // c.1. Подгонка parent-facing коннектора
                            SmartConLogger.Info($"[Chain+]   c.1 TrySetConnectorRadius(elemId={elemId.Value}, " +
                                $"connIdx={edge.Value.ElemConnIdx}, target={targetRadius * 304.8:F2}mm)...");
                            bool setResult = _paramResolver.TrySetConnectorRadius(
                                doc, elemId, edge.Value.ElemConnIdx, targetRadius);
                            SmartConLogger.Info($"[Chain+]   c.1 TrySetConnectorRadius → {(setResult ? "OK" : "FAILED")}");

                            // c.2. Для FamilyInstance: подогнать ВСЕ коннекторы элемента в графе
                            var elem = doc.GetElement(elemId);
                            if (elem is FamilyInstance)
                            {
                                var allElemConns = _connSvc.GetAllConnectors(doc, elemId);
                                foreach (var c in allElemConns)
                                {
                                    if (c.ConnectorIndex != edge.Value.ElemConnIdx)
                                    {
                                        bool inGraph = false;
                                        foreach (var e in graph.Edges)
                                        {
                                            if ((comparer.Equals(e.FromElementId, elemId) && e.FromConnectorIndex == c.ConnectorIndex) ||
                                                (comparer.Equals(e.ToElementId, elemId) && e.ToConnectorIndex == c.ConnectorIndex))
                                            {
                                                inGraph = true;
                                                break;
                                            }
                                        }
                                        if (inGraph)
                                        {
                                            SmartConLogger.Info($"[Chain+]   c.2 TrySetConnectorRadius(connIdx={c.ConnectorIndex}, " +
                                                $"currentR={c.Radius * 304.8:F2}mm, target={targetRadius * 304.8:F2}mm)...");
                                            bool r2 = _paramResolver.TrySetConnectorRadius(doc, elemId, c.ConnectorIndex, targetRadius);
                                            SmartConLogger.Info($"[Chain+]   c.2 → {(r2 ? "OK" : "FAILED")}");
                                        }
                                    }
                                }
                            }

                            doc.Regenerate();

                            // c.3. ВЕРИФИКАЦИЯ фактического радиуса
                            elemRefreshed = _connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
                            double actualRadius = elemRefreshed?.Radius ?? 0;
                            double actualDn = System.Math.Round(actualRadius * 2.0 * 304.8);
                            double verifyDelta = System.Math.Abs(targetRadius - actualRadius);

                            SmartConLogger.Info($"[Chain+]   c.3 Верификация: actualR={actualRadius * 304.8:F2}mm " +
                                $"(DN{actualDn}), targetR={targetRadius * 304.8:F2}mm (DN{targetDn}), " +
                                $"delta={verifyDelta * 304.8:F4}mm, match={verifyDelta <= 1e-5}");

                            if (elemRefreshed is not null && verifyDelta > 1e-5)
                            {
                                SmartConLogger.Info($"[Chain+]   c.3 Подгонка НЕ удалась → InsertReducer...");
                                reducerId = _networkMover.InsertReducer(doc, parentProxy, elemRefreshed);
                                if (reducerId is not null)
                                {
                                    SmartConLogger.Info($"[Chain+]   c.3 Reducer вставлен: id={reducerId.Value}");
                                    _snapshotStore.TrackReducer(elemId, reducerId);
                                }
                                else
                                {
                                    SmartConLogger.Warn($"[Chain+]   c.3 Reducer не найден в маппинге!");
                                }
                            }
                        }
                        else
                        {
                            SmartConLogger.Info($"[Chain+]   c. Размеры совпадают, подгонка не нужна");
                        }

                        doc.Regenerate();
                    }

                    // ── d. Align: ПОСЛЕ смены размера — геометрия актуальна ──
                    // Обновляем parentProxy (parent мог сместиться при Regenerate)
                    parentProxy = _connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
                    var elemProxyForAlign = _connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);

                    // Если вставлен reducer — выравниваем к reducer.conn2, иначе к parent
                    ConnectorProxy? alignTarget = parentProxy;

                    if (reducerId is not null)
                    {
                        // Reducer уже выровнен к parent (InsertReducer делает AlignFittingToStatic).
                        // Нужно выровнять child элемент ко ВТОРОМУ коннектору reducer-а.
                        var rConns = _connSvc.GetAllFreeConnectors(doc, reducerId);
                        if (rConns.Count >= 2 && parentProxy is not null)
                        {
                            var rConn1 = rConns
                                .OrderBy(rc => VectorUtils.DistanceTo(rc.OriginVec3, parentProxy.OriginVec3))
                                .First();
                            alignTarget = rConns.FirstOrDefault(rc => rc.ConnectorIndex != rConn1.ConnectorIndex);
                            SmartConLogger.Info($"[Chain+]   d. Align target = reducer conn2 " +
                                $"(R={alignTarget?.Radius * 304.8:F2}mm, " +
                                $"origin=({alignTarget?.Origin.X:F4},{alignTarget?.Origin.Y:F4},{alignTarget?.Origin.Z:F4}))");
                        }
                    }

                    if (alignTarget is not null && elemProxyForAlign is not null)
                    {
                        SmartConLogger.Info($"[Chain+]   d. Align: elem R={elemProxyForAlign.Radius * 304.8:F2}mm " +
                            $"→ target R={alignTarget.Radius * 304.8:F2}mm");

                        var alignResult = ConnectorAligner.ComputeAlignment(
                            alignTarget.OriginVec3, alignTarget.BasisZVec3, alignTarget.BasisXVec3,
                            elemProxyForAlign.OriginVec3, elemProxyForAlign.BasisZVec3, elemProxyForAlign.BasisXVec3);

                        if (!VectorUtils.IsZero(alignResult.InitialOffset))
                        {
                            SmartConLogger.Info($"[Chain+]   d. Move offset=({alignResult.InitialOffset.X * 304.8:F2}," +
                                $"{alignResult.InitialOffset.Y * 304.8:F2},{alignResult.InitialOffset.Z * 304.8:F2})mm");
                            _transformSvc.MoveElement(doc, elemId, alignResult.InitialOffset);
                        }
                        if (alignResult.BasisZRotation is { } bzRot)
                        {
                            SmartConLogger.Info($"[Chain+]   d. RotateBZ angle={bzRot.AngleRadians * 180 / System.Math.PI:F2}°");
                            _transformSvc.RotateElement(doc, elemId,
                                alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);
                        }
                        if (alignResult.BasisXSnap is { } bxSnap)
                        {
                            SmartConLogger.Info($"[Chain+]   d. RotateBX angle={bxSnap.AngleRadians * 180 / System.Math.PI:F2}°");
                            _transformSvc.RotateElement(doc, elemId,
                                alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);
                        }

                        doc.Regenerate();

                        // Коррекция позиции (после поворотов Origin мог сместиться)
                        var refreshedAfterAlign = _connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
                        if (refreshedAfterAlign is not null)
                        {
                            var correction = alignTarget.OriginVec3 - refreshedAfterAlign.OriginVec3;
                            if (!VectorUtils.IsZero(correction))
                            {
                                SmartConLogger.Info($"[Chain+]   d. PosCorrection dist={VectorUtils.Length(correction) * 304.8:F3}mm");
                                _transformSvc.MoveElement(doc, elemId, correction);
                            }
                        }
                        doc.Regenerate();
                    }

                    // ── e. ConnectTo ──
                    if (reducerId is not null && parentProxy is not null)
                    {
                        SmartConLogger.Info($"[Chain+]   e. ConnectTo через reducer id={reducerId.Value}");
                        var rConnsForConnect = _connSvc.GetAllFreeConnectors(doc, reducerId);
                        SmartConLogger.Info($"[Chain+]   e. Reducer free conns: {rConnsForConnect.Count}");
                        var rConn1 = rConnsForConnect
                            .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, parentProxy.OriginVec3))
                            .FirstOrDefault();
                        var rConn2 = rConnsForConnect.FirstOrDefault(c => c.ConnectorIndex != (rConn1?.ConnectorIndex ?? -1));

                        // Соединить parent ↔ reducer_conn1
                        if (rConn1 is not null)
                        {
                            SmartConLogger.Info($"[Chain+]   e. ConnectTo: parent({edge.Value.ParentId.Value}:{edge.Value.ParentConnIdx}) ↔ reducer({reducerId.Value}:{rConn1.ConnectorIndex})");
                            _connSvc.ConnectTo(doc, edge.Value.ParentId, edge.Value.ParentConnIdx,
                                reducerId, rConn1.ConnectorIndex);
                        }
                        // Соединить reducer_conn2 ↔ child
                        if (rConn2 is not null)
                        {
                            SmartConLogger.Info($"[Chain+]   e. ConnectTo: reducer({reducerId.Value}:{rConn2.ConnectorIndex}) ↔ elem({elemId.Value}:{edge.Value.ElemConnIdx})");
                            _connSvc.ConnectTo(doc, reducerId, rConn2.ConnectorIndex,
                                elemId, edge.Value.ElemConnIdx);
                        }
                    }
                    else
                    {
                        // Прямое соединение
                        SmartConLogger.Info($"[Chain+]   e. ConnectTo прямое: parent({edge.Value.ParentId.Value}:{edge.Value.ParentConnIdx}) ↔ elem({elemId.Value}:{edge.Value.ElemConnIdx})");
                        _connSvc.ConnectTo(doc, edge.Value.ParentId, edge.Value.ParentConnIdx,
                            elemId, edge.Value.ElemConnIdx);
                    }

                    SmartConLogger.Info($"[Chain+] ── Элемент {elemId.Value} готов ──");
                }

                doc.Regenerate();
            });

            ChainDepth = nextLevel;
            UpdateChainUI();
            StatusMessage = $"Уровень {nextLevel} присоединён";
            SmartConLogger.Info($"[Chain+] ═══ УРОВЕНЬ {nextLevel} ГОТОВ ═══");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Chain+] Ошибка: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = $"Ошибка цепочки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanIncrementChain()
        => IsSessionActive && !IsBusy
        && _chainGraph is not null
        && ChainDepth < _chainGraph.MaxLevel
        && ChainDepth < MaxChainLevel;

    [RelayCommand(CanExecute = nameof(CanDecrementChain))]
    private void DecrementChainDepth()
    {
        if (_chainGraph is null || ChainDepth <= 0) return;
        var graph = _chainGraph;
        var levelElements = graph.Levels[ChainDepth];

        IsBusy = true;
        StatusMessage = $"Откат уровня {ChainDepth}…";

        try
        {
            SmartConLogger.Info($"[Chain−] ═══ ОТКАТ УРОВНЯ {ChainDepth} ═══ ({levelElements.Count} элементов)");

            _groupSession!.RunInTransaction($"Цепочка: откат уровня {ChainDepth}", doc =>
            {
                foreach (var elemId in levelElements)
                {
                    var elemRaw = doc.GetElement(elemId);
                    SmartConLogger.Info($"[Chain−] ── Элемент id={elemId.Value} '{elemRaw?.Name}' ({elemRaw?.GetType().Name}) ──");

                    // ── a. Disconnect от всех текущих соединений ──
                    var allConns = _connSvc.GetAllConnectors(doc, elemId);
                    int disconnected = 0;
                    foreach (var c in allConns)
                    {
                        if (!c.IsFree)
                        {
                            _connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex);
                            disconnected++;
                        }
                    }
                    SmartConLogger.Info($"[Chain−]   a. Disconnect: {disconnected} соединений разорвано");

                    // ── b. Удалить reducer-ы ──
                    var reducers = _snapshotStore.GetReducers(elemId);
                    SmartConLogger.Info($"[Chain−]   b. Reducers для удаления: {reducers.Count}");
                    foreach (var reducerId in reducers)
                    {
                        SmartConLogger.Info($"[Chain−]   b. Удаление reducer id={reducerId.Value}");
                        var rConns = _connSvc.GetAllConnectors(doc, reducerId);
                        foreach (var rc in rConns)
                        {
                            if (!rc.IsFree)
                                _connSvc.DisconnectAllFromConnector(doc, reducerId, rc.ConnectorIndex);
                        }
                        _fittingInsertSvc.DeleteElement(doc, reducerId);
                    }

                    // ── c. Восстановить размер и позицию из snapshot ──
                    var snapshot = _snapshotStore.Get(elemId);
                    if (snapshot is null)
                    {
                        SmartConLogger.Warn($"[Chain−]   c. Snapshot не найден → skip");
                        continue;
                    }
                    var elem = doc.GetElement(elemId);
                    SmartConLogger.Info($"[Chain−]   c. Восстановление: isMepCurve={snapshot.IsMepCurve}, " +
                        $"snapR={snapshot.ConnectorRadius * 304.8:F2}mm (DN{System.Math.Round(snapshot.ConnectorRadius * 2.0 * 304.8)}), " +
                        $"symbolId={snapshot.FamilySymbolId?.Value}");

                    if (elem is MEPCurve mc)
                    {
                        // Шаг 1: ВСЕГДА восстановить ДИАМЕТР (работает для Pipe, FlexPipe и любого MEPCurve)
                        var diamParam = mc.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                        if (diamParam is not null && !diamParam.IsReadOnly)
                        {
                            double targetDiam = snapshot.ConnectorRadius * 2.0;
                            SmartConLogger.Info($"[Chain−]   c. MEPCurve: restore diameter={targetDiam * 304.8:F2}mm");
                            diamParam.Set(targetDiam);
                        }
                        else
                        {
                            SmartConLogger.Info($"[Chain−]   c. MEPCurve: TrySetConnectorRadius fallback...");
                            var conns = _connSvc.GetAllConnectors(doc, elemId);
                            if (conns.Count > 0)
                                _paramResolver.TrySetConnectorRadius(doc, elemId, conns[0].ConnectorIndex, snapshot.ConnectorRadius);
                        }
                        doc.Regenerate();

                        // Шаг 2: Восстановить позицию ТОЛЬКО для Line-based curves (Pipe)
                        // FlexPipe имеет NurbSpline — не восстанавливаем позицию, он адаптируется при соединении
                        if (snapshot.CurveStart is not null && snapshot.CurveEnd is not null
                            && mc.Location is LocationCurve lc && lc.Curve is Line)
                        {
                            SmartConLogger.Info($"[Chain−]   c. MEPCurve: restore curve " +
                                $"({snapshot.CurveStart.X:F4},{snapshot.CurveStart.Y:F4},{snapshot.CurveStart.Z:F4}) → " +
                                $"({snapshot.CurveEnd.X:F4},{snapshot.CurveEnd.Y:F4},{snapshot.CurveEnd.Z:F4})");
                            try
                            {
                                lc.Curve = Line.CreateBound(snapshot.CurveStart, snapshot.CurveEnd);
                            }
                            catch (Exception exCurve)
                            {
                                SmartConLogger.Warn($"[Chain−]   c. MEPCurve: Line.CreateBound failed: {exCurve.Message}");
                            }
                        }
                        else if (snapshot.FirstConnectorOrigin is not null)
                        {
                            // FlexPipe или другой MEPCurve без Line — перемещаем к исходной позиции коннектора
                            var currentConn = _connSvc.RefreshConnector(doc, elemId, snapshot.FirstConnectorIndex);
                            if (currentConn is not null)
                            {
                                var offset = new Vec3(
                                    snapshot.FirstConnectorOrigin.X - currentConn.Origin.X,
                                    snapshot.FirstConnectorOrigin.Y - currentConn.Origin.Y,
                                    snapshot.FirstConnectorOrigin.Z - currentConn.Origin.Z);
                                if (!VectorUtils.IsZero(offset))
                                {
                                    SmartConLogger.Info($"[Chain−]   c. MEPCurve(FlexPipe): MoveElement to snap connector, " +
                                        $"dist={VectorUtils.Length(offset) * 304.8:F2}mm");
                                    _transformSvc.MoveElement(doc, elemId, offset);
                                }
                            }
                        }
                        else
                        {
                            SmartConLogger.Info($"[Chain−]   c. MEPCurve: skip restore (no position data)");
                        }
                        doc.Regenerate();
                    }
                    else if (elem is FamilyInstance fi && snapshot.FiOrigin is not null)
                    {
                        // Шаг 1a: Восстановить FamilySymbol если изменился
                        if (snapshot.FamilySymbolId is not null && fi.Symbol.Id != snapshot.FamilySymbolId)
                        {
                            SmartConLogger.Info($"[Chain−]   c. FI: ChangeTypeId {fi.Symbol.Id.Value} → {snapshot.FamilySymbolId.Value}");
                            fi.ChangeTypeId(snapshot.FamilySymbolId);
                        }

                        // Шаг 1b: Восстановить размер КАЖДОГО коннектора к его ИСХОДНОМУ радиусу
                        // Используем per-connector ConnectorRadii из snapshot, а не единый ConnectorRadius.
                        // Критично для многопортовых элементов (коллекторы, гребёнки) с разными DN.
                        var fiConns = _connSvc.GetAllConnectors(doc, elemId);
                        foreach (var fc in fiConns)
                        {
                            double targetR = snapshot.ConnectorRadii.TryGetValue(fc.ConnectorIndex, out var snapR)
                                ? snapR
                                : snapshot.ConnectorRadius; // fallback
                            double delta = System.Math.Abs(fc.Radius - targetR);
                            if (delta > 1e-5)
                            {
                                SmartConLogger.Info($"[Chain−]   c. FI: TrySetConnectorRadius(connIdx={fc.ConnectorIndex}, " +
                                    $"current={fc.Radius * 304.8:F2}mm → target={targetR * 304.8:F2}mm)");
                                _paramResolver.TrySetConnectorRadius(doc, elemId, fc.ConnectorIndex, targetR);
                            }
                        }
                        doc.Regenerate();

                        // Шаг 2: Восстановить позицию
                        if (fi.Location is LocationPoint lp)
                        {
                            SmartConLogger.Info($"[Chain−]   c. FI: set Point=({snapshot.FiOrigin.X:F4},{snapshot.FiOrigin.Y:F4},{snapshot.FiOrigin.Z:F4})");
                            lp.Point = snapshot.FiOrigin;
                        }
                        doc.Regenerate();

                        // Шаг 3: Восстановить ориентацию НАПРЯМУЮ (без ConnectorAligner!)
                        // ConnectorAligner делает BasisZ АНТИПАРАЛЛЕЛЬНЫМИ (для соединения),
                        // а нам нужно вернуть BasisZ В ТО ЖЕ направление → прямой поворот.
                        var currentT = fi.GetTransform();
                        var curBZ = new Vec3(currentT.BasisZ.X, currentT.BasisZ.Y, currentT.BasisZ.Z);
                        var snapBZ = new Vec3(snapshot.FiBasisZ!.X, snapshot.FiBasisZ.Y, snapshot.FiBasisZ.Z);
                        SmartConLogger.Info($"[Chain−]   c. FI: curBZ=({curBZ.X:F3},{curBZ.Y:F3},{curBZ.Z:F3}), " +
                            $"snapBZ=({snapBZ.X:F3},{snapBZ.Y:F3},{snapBZ.Z:F3})");

                        // Поворот BasisZ: от текущего к целевому (ПАРАЛЛЕЛЬНО, не антипараллельно)
                        double angleBZ = VectorUtils.AngleBetween(curBZ, snapBZ);
                        if (angleBZ > 1e-6 && angleBZ < System.Math.PI - 1e-6)
                        {
                            var axisBZ = VectorUtils.CrossProduct(curBZ, snapBZ);
                            double axisLen = VectorUtils.Length(axisBZ);
                            if (axisLen > 1e-10)
                            {
                                axisBZ = new Vec3(axisBZ.X / axisLen, axisBZ.Y / axisLen, axisBZ.Z / axisLen);
                                SmartConLogger.Info($"[Chain−]   c. FI: RotBZ angle={angleBZ * 180 / System.Math.PI:F2}°");
                                _transformSvc.RotateElement(doc, elemId,
                                    new Vec3(snapshot.FiOrigin.X, snapshot.FiOrigin.Y, snapshot.FiOrigin.Z),
                                    axisBZ, angleBZ);
                                doc.Regenerate();
                            }
                        }
                        else if (angleBZ >= System.Math.PI - 1e-6)
                        {
                            // BasisZ антипараллельны — поворот на 180° вокруг любой перпендикулярной оси
                            var perpAxis = System.Math.Abs(curBZ.Z) < 0.9
                                ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
                            SmartConLogger.Info($"[Chain−]   c. FI: RotBZ 180° (antiparallel)");
                            _transformSvc.RotateElement(doc, elemId,
                                new Vec3(snapshot.FiOrigin.X, snapshot.FiOrigin.Y, snapshot.FiOrigin.Z),
                                perpAxis, System.Math.PI);
                            doc.Regenerate();
                        }

                        // Поворот BasisX: после BasisZ восстановлен, подгоняем BasisX
                        currentT = fi.GetTransform();
                        var curBX = new Vec3(currentT.BasisX.X, currentT.BasisX.Y, currentT.BasisX.Z);
                        var snapBX = new Vec3(snapshot.FiBasisX!.X, snapshot.FiBasisX.Y, snapshot.FiBasisX.Z);
                        double angleBX = VectorUtils.AngleBetween(curBX, snapBX);
                        if (angleBX > 1e-4)
                        {
                            // Ось вращения = BasisZ (уже восстановленный)
                            var rotAxis = new Vec3(currentT.BasisZ.X, currentT.BasisZ.Y, currentT.BasisZ.Z);
                            // Определяем знак угла через cross product
                            var cross = VectorUtils.CrossProduct(curBX, snapBX);
                            double dot = cross.X * rotAxis.X + cross.Y * rotAxis.Y + cross.Z * rotAxis.Z;
                            double signedAngle = dot >= 0 ? angleBX : -angleBX;
                            SmartConLogger.Info($"[Chain−]   c. FI: RotBX angle={signedAngle * 180 / System.Math.PI:F2}°");
                            _transformSvc.RotateElement(doc, elemId,
                                new Vec3(snapshot.FiOrigin.X, snapshot.FiOrigin.Y, snapshot.FiOrigin.Z),
                                rotAxis, signedAngle);
                            doc.Regenerate();
                        }

                        // Шаг 4: Финальная коррекция позиции
                        if (fi.Location is LocationPoint lp2)
                        {
                            var correction = new Vec3(
                                snapshot.FiOrigin.X - lp2.Point.X,
                                snapshot.FiOrigin.Y - lp2.Point.Y,
                                snapshot.FiOrigin.Z - lp2.Point.Z);
                            if (!VectorUtils.IsZero(correction))
                            {
                                SmartConLogger.Info($"[Chain−]   c. FI: final correction={VectorUtils.Length(correction) * 304.8:F2}mm");
                                _transformSvc.MoveElement(doc, elemId, correction);
                            }
                            doc.Regenerate();
                        }
                    }

                    // ── d. Восстановить исходные соединения ──
                    SmartConLogger.Info($"[Chain−]   d. Восстановление соединений: {snapshot.Connections.Count} записей");
                    foreach (var connRecord in snapshot.Connections)
                    {
                        var neighborId = connRecord.NeighborElementId;
                        bool inChain = IsInCurrentChain(neighborId, ChainDepth - 1, graph);
                        SmartConLogger.Info($"[Chain−]   d. connRecord: this={connRecord.ThisElementId.Value}:{connRecord.ThisConnectorIndex} " +
                            $"↔ neighbor={neighborId.Value}:{connRecord.NeighborConnectorIndex}, inChain={inChain}");
                        if (inChain) continue;

                        var neighborConn = _connSvc.RefreshConnector(doc, neighborId, connRecord.NeighborConnectorIndex);
                        if (neighborConn is null)
                        {
                            SmartConLogger.Warn($"[Chain−]   d. neighborConn=null → skip");
                            continue;
                        }
                        if (!neighborConn.IsFree)
                        {
                            SmartConLogger.Info($"[Chain−]   d. neighbor busy → disconnect first");
                            _connSvc.DisconnectAllFromConnector(doc, neighborId, connRecord.NeighborConnectorIndex);
                        }

                        try
                        {
                            _connSvc.ConnectTo(doc,
                                connRecord.ThisElementId, connRecord.ThisConnectorIndex,
                                connRecord.NeighborElementId, connRecord.NeighborConnectorIndex);
                            SmartConLogger.Info($"[Chain−]   d. ConnectTo OK");
                        }
                        catch (Exception exConn)
                        {
                            SmartConLogger.Warn($"[Chain−]   d. ConnectTo FAILED: {exConn.Message}");
                        }
                    }
                }

                doc.Regenerate();
            });

            ChainDepth--;
            UpdateChainUI();
            StatusMessage = $"Уровень {ChainDepth + 1} отсоединён";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Chain−] Ошибка: {ex.Message}");
            StatusMessage = $"Ошибка отката: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDecrementChain()
        => IsSessionActive && !IsBusy && ChainDepth > 0;

    // ── Chain helpers ─────────────────────────────────────────────────────────

    private ElementSnapshot CaptureSnapshot(Document doc, ElementId elemId, ConnectionGraph graph)
    {
        var elem = doc.GetElement(elemId);
        bool isMepCurve = elem is MEPCurve;

        XYZ? fiOrigin = null, fiBasisX = null, fiBasisY = null, fiBasisZ = null;
        XYZ? curveStart = null, curveEnd = null;
        ElementId? familySymbolId = null;

        if (elem is FamilyInstance fi)
        {
            var t = fi.GetTransform();
            fiOrigin = t.Origin;
            fiBasisX = t.BasisX;
            fiBasisY = t.BasisY;
            fiBasisZ = t.BasisZ;
            familySymbolId = fi.Symbol.Id;
        }

        if (elem is MEPCurve mc && mc.Location is LocationCurve lc && lc.Curve is Line line)
        {
            curveStart = line.GetEndPoint(0);
            curveEnd = line.GetEndPoint(1);
        }

        // Радиус и позиция коннекторов
        double connRadius = 0;
        XYZ? firstConnOrigin = null;
        int firstConnIdx = -1;
        var connRadiiDict = new Dictionary<int, double>();
        var conns = _connSvc.GetAllConnectors(doc, elemId);
        if (conns.Count > 0)
        {
            connRadius = conns[0].Radius;
            firstConnOrigin = conns[0].Origin;
            firstConnIdx = conns[0].ConnectorIndex;
        }
        foreach (var c in conns)
            connRadiiDict[c.ConnectorIndex] = c.Radius;

        return new ElementSnapshot
        {
            ElementId = elemId,
            IsMepCurve = isMepCurve,
            FiOrigin = fiOrigin,
            FiBasisX = fiBasisX,
            FiBasisY = fiBasisY,
            FiBasisZ = fiBasisZ,
            CurveStart = curveStart,
            CurveEnd = curveEnd,
            FirstConnectorOrigin = firstConnOrigin,
            FirstConnectorIndex = firstConnIdx,
            ConnectorRadius = connRadius,
            ConnectorRadii = connRadiiDict,
            FamilySymbolId = familySymbolId,
            Connections = graph.GetOriginalConnections(elemId),
        };
    }

    private record struct ParentEdge(ElementId ParentId, int ParentConnIdx, int ElemConnIdx);

    private static ParentEdge? FindEdgeToParent(ElementId elemId, int level, ConnectionGraph graph)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        var parentLevel = graph.Levels[level - 1];
        var parentIds = new HashSet<ElementId>(parentLevel, comparer);

        foreach (var edge in graph.Edges)
        {
            if (comparer.Equals(edge.ToElementId, elemId) && parentIds.Contains(edge.FromElementId))
                return new ParentEdge(edge.FromElementId, edge.FromConnectorIndex, edge.ToConnectorIndex);
            if (comparer.Equals(edge.FromElementId, elemId) && parentIds.Contains(edge.ToElementId))
                return new ParentEdge(edge.ToElementId, edge.ToConnectorIndex, edge.FromConnectorIndex);
        }
        return null;
    }

    private static bool IsInCurrentChain(ElementId elemId, int maxLevel, ConnectionGraph graph)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        for (int level = 0; level <= maxLevel && level < graph.Levels.Count; level++)
        {
            foreach (var id in graph.Levels[level])
            {
                if (comparer.Equals(id, elemId))
                    return true;
            }
        }
        return false;
    }

    private void UpdateChainUI()
    {
        if (_chainGraph is null || _chainGraph.TotalChainElements == 0)
        {
            ChainDepthHint = "нет цепочки";
            HasChain = false;
            IncrementChainDepthCommand.NotifyCanExecuteChanged();
            DecrementChainDepthCommand.NotifyCanExecuteChanged();
            return;
        }
        HasChain = true;
        int total = _chainGraph.TotalChainElements;
        int maxLvl = _chainGraph.MaxLevel;
        int attached = 0;
        for (int l = 1; l <= ChainDepth && l < _chainGraph.Levels.Count; l++)
            attached += _chainGraph.Levels[l].Count;

        ChainDepthHint = $"{attached}/{total} в {ChainDepth}/{maxLvl} ур.";

        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
    }

    private ConnectionTypeCode ResolveDynamicTypeFromRule(FittingMappingRule? rule)
    {
        if (rule is null) return default;
        if (rule.FromType.Value == _ctx.StaticConnector.ConnectionTypeCode.Value)
            return rule.ToType;
        return rule.FromType;
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
            _activeFittingRule = null;
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

        _activeFittingRule = fitting.Rule;
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        if (!EnsureFittingCtcForInsert(fitting))
        {
            StatusMessage = "Типы коннекторов не назначены";
            return;
        }

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

            doc.Regenerate();

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedId, _ctx.StaticConnector, _transformSvc, _connSvc,
                dynamicTypeCode: dynCtc);

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

        var newFitConn2 = SizeFittingConnectors(_doc, insertedId, fitConn2, adjustDynamicToFit: true);
        if (newFitConn2 is not null)
            _activeFittingConn2 = newFitConn2;
    }

    private void InsertFittingSilentNoDynamicAdjust(FittingCardItem fitting)
    {
        if (fitting.IsDirectConnect)
        {
            _activeFittingRule = null;
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

        _activeFittingRule = fitting.Rule;
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        if (!EnsureFittingCtcForInsert(fitting))
        {
            StatusMessage = "Типы коннекторов не назначены";
            return;
        }

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

            doc.Regenerate();

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedId, _ctx.StaticConnector, _transformSvc, _connSvc,
                dynamicTypeCode: dynCtc);

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

        var newFitConn2 = SizeFittingConnectors(_doc, insertedId, fitConn2, adjustDynamicToFit: false);
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
        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private ConnectorProxy? SizeFittingConnectors(Document doc, ElementId fittingId, ConnectorProxy? fitConn2, bool adjustDynamicToFit = true)
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
                // Используем ТЕКУЩИЙ радиус dynamic (может быть изменён через ChangeDynamicSize).
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

                // Подгоняем dynamic под размер dynamic-коннектора фитинга
                const double eps = 1e-6;
                var dynId      = _ctx.DynamicConnector.OwnerElementId;
                var dynConnIdx = _ctx.DynamicConnector.ConnectorIndex;
                double actualDynRadius = _activeDynamic?.Radius ?? currentDynRadius;
                if (adjustDynamicToFit && System.Math.Abs(achievedDynRadius - actualDynRadius) > eps)
                {
                    SmartConLogger.Info($"[SizeFitting] Подгонка dynamic: {actualDynRadius * 304.8:F2}мм → {achievedDynRadius * 304.8:F2}мм");
                    _groupSession.RunInTransaction("PipeConnect — Подгонка dynamic под фитинг", txDoc =>
                    {
                        _paramResolver.TrySetConnectorRadius(txDoc, dynId, dynConnIdx, achievedDynRadius);
                        txDoc.Regenerate();
                    });
                }
                else if (!adjustDynamicToFit)
                {
                    SmartConLogger.Info($"[SizeFitting] Пропуск подгонки dynamic (adjustDynamicToFit=false). Фактический dynamic={actualDynRadius * 304.8:F2}мм, фитинг достиг={achievedDynRadius * 304.8:F2}мм");
                }
            }
            else
            {
                // Для фитингов с независимыми параметрами коннекторов:
                // conn1 (к static) → staticRadius, conn2 (к dynamic) → ТЕКУЩИЙ радиус dynamic
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
                    txDoc, fittingId, _ctx.StaticConnector, _transformSvc, _connSvc,
                    dynamicTypeCode: ResolveDynamicTypeFromRule(_activeFittingRule));

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

    // ── Fitting CTC setup (before insert) ──────────────────────────────────

    private bool EnsureFittingCtcForInsert(FittingCardItem fitting)
    {
        if (_doc.IsModifiable) return true;

        var primary = fitting.PrimaryFitting;
        if (primary is null) return true;

        var symbol = FindFamilySymbol(_doc, primary.FamilyName, primary.SymbolName);
        if (symbol is null) return true;

        if (IsFittingCtcDefined(symbol)) return true;

        var types = _mappingRepo.GetConnectorTypes();
        if (types.Count == 0) return true;

        var items = BuildConnectorItems(symbol, types, fitting.Rule);

        SmartConLogger.Info($"[CTC] Фитинг '{symbol.Family.Name}' ({symbol.Name}): CTC не задан → диалог");

        if (!_dialogSvc.ShowFittingCtcSetup(
                symbol.Family.Name, symbol.Name, items, types))
        {
            _dialogSvc.ShowWarning("SmartCon",
                "Типы коннекторов не назначены. Фитинг не будет вставлен.");
            return false;
        }

        ApplyFittingCtcToFamily(symbol, items);
        return true;
    }

    private bool IsFittingCtcDefined(FamilySymbol symbol)
    {
        Document? familyDoc = null;
        try
        {
            familyDoc = _doc.EditFamily(symbol.Family);
            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            if (connElems.Count < 2) return true;

            foreach (var ce in connElems)
            {
                var desc = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
                var ctc = ConnectionTypeCode.Parse(desc);
                if (!ctc.IsDefined) return false;
            }

            return true;
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    private List<FittingCtcSetupItem> BuildConnectorItems(
        FamilySymbol symbol, IReadOnlyList<ConnectorTypeDefinition> types, FittingMappingRule rule)
    {
        Document? familyDoc = null;
        try
        {
            familyDoc = _doc.EditFamily(symbol.Family);

            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            var items = new List<FittingCtcSetupItem>();

            var staticCTC = _ctx.StaticConnector.ConnectionTypeCode;
            ConnectionTypeCode? preSelectForStatic = null;
            ConnectionTypeCode? preSelectForDynamic = null;
            if (rule.FromType.Value == staticCTC.Value)
            {
                preSelectForStatic = rule.FromType;
                preSelectForDynamic = rule.ToType;
            }
            else if (rule.ToType.Value == staticCTC.Value)
            {
                preSelectForStatic = rule.ToType;
                preSelectForDynamic = rule.FromType;
            }

            for (int i = 0; i < connElems.Count; i++)
            {
                var ce = connElems[i];
                var desc = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
                var currentCtc = ConnectionTypeCode.Parse(desc);

                string paramName = GetConnectorParamName(ce, familyDoc);
                double diamMm = ce.Radius * 2.0 * 304.8;

                ConnectorTypeDefinition? preSelect = null;
                if (preSelectForStatic.HasValue && preSelectForDynamic.HasValue)
                {
                    var code = i == 0 ? preSelectForStatic.Value : preSelectForDynamic.Value;
                    preSelect = types.FirstOrDefault(t => t.Code == code.Value);
                }

                items.Add(new FittingCtcSetupItem
                {
                    ConnectorIndex = i,
                    ParameterName = paramName,
                    DiameterMm = diamMm,
                    SelectedType = currentCtc.IsDefined
                        ? types.FirstOrDefault(t => t.Code == currentCtc.Value)
                        : null,
                    PreSelectedType = preSelect
                });
            }

            return items;
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    private static string GetConnectorParamName(ConnectorElement ce, Document familyDoc)
    {
        var radiusParam = ce.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
        var diamParam = ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);

        foreach (FamilyParameter fp in familyDoc.FamilyManager.GetParameters())
        {
            if (fp.AssociatedParameters.Size == 0) continue;
            try
            {
                foreach (Parameter assoc in fp.AssociatedParameters)
                {
                    bool idMatch = (radiusParam is not null && assoc.Id == radiusParam.Id)
                                || (diamParam is not null && assoc.Id == diamParam.Id);
                    bool elemMatch = assoc.Element?.Id == ce.Id;

                    if (idMatch && elemMatch)
                        return fp.Definition?.Name ?? string.Empty;
                }
            }
            catch { }
        }

        return string.Empty;
    }

    private void ApplyFittingCtcToFamily(FamilySymbol symbol, List<FittingCtcSetupItem> items)
    {
        if (_doc.IsModifiable) return;

        Document? familyDoc = null;
        try
        {
            familyDoc = _doc.EditFamily(symbol.Family);

            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            bool anyWritten = false;

            {
                using var familyTx = new Transaction(familyDoc, "SetFittingCtcDescriptions");
                familyTx.Start();

                for (int i = 0; i < items.Count && i < connElems.Count; i++)
                {
                    if (items[i].SelectedType is null) continue;

                    var ce = connElems[i];
                    var typeDef = items[i].SelectedType!;
                    var value = $"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}";

                    var descParam = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);

                    if (descParam is not null && !descParam.IsReadOnly)
                    {
                        anyWritten |= descParam.Set(value);
                    }
                    else if (descParam is not null)
                    {
                        anyWritten |= SetDrivingFamilyParameter(
                            familyDoc.FamilyManager, ce, descParam, value);
                    }
                }

                if (anyWritten)
                    familyTx.Commit();
            }

            if (anyWritten)
            {
                familyDoc.LoadFamily(_doc, new FamilyLoadOptions());
                SmartConLogger.Info($"[CTC] CTC записан для '{symbol.Family.Name}'");
            }
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    private static bool SetDrivingFamilyParameter(
        FamilyManager fm, ConnectorElement connElem, Parameter connParam, string value)
    {
        FamilyParameter? drivingFp = null;

        foreach (FamilyParameter fp in fm.Parameters)
        {
            try
            {
                foreach (Parameter assoc in fp.AssociatedParameters)
                {
                    if (assoc.Id == connParam.Id && assoc.Element?.Id == connElem.Id)
                    {
                        drivingFp = fp;
                        break;
                    }
                }
            }
            catch { }
            if (drivingFp is not null) break;
        }

        if (drivingFp is null) return false;
        if (!string.IsNullOrEmpty(drivingFp.Formula)) return false;

        try
        {
            if (!drivingFp.IsInstance)
            {
                foreach (FamilyType ft in fm.Types)
                {
                    fm.CurrentType = ft;
                    fm.Set(drivingFp, value);
                }
            }
            else
            {
                fm.Set(drivingFp, value);
            }
            return true;
        }
        catch { return false; }
    }

    private static FamilySymbol? FindFamilySymbol(Document doc, string familyName, string symbolName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(s.Family.Name, familyName, StringComparison.OrdinalIgnoreCase) &&
                (symbolName == "*" || string.Equals(s.Name, symbolName, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Autodesk.Revit.DB.Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
