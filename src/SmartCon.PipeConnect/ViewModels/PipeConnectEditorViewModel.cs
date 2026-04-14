using System.Collections.ObjectModel;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel : ObservableObject
{
    private readonly ITransactionService _txService;
    private readonly Document _doc;
    private readonly IConnectorService _connSvc;
    private readonly ITransformService _transformSvc;
    private readonly IFittingInsertService _fittingInsertSvc;
    private readonly IParameterResolver _paramResolver;
    private readonly IDynamicSizeResolver _sizeResolver;
    private readonly INetworkMover _networkMover;
    private readonly IFittingMappingRepository _mappingRepo;
    private readonly IDialogService _dialogSvc;
    private readonly IFamilyConnectorService _familyConnSvc;
    private readonly ChainOperationHandler _chainOpHandler;
    private readonly ConnectExecutor _connectExecutor;
    private readonly PipeConnectInitHandler _initHandler;
    private readonly PipeConnectRotationHandler _rotationHandler;
    private readonly PipeConnectSizeHandler _sizeHandler;
    private readonly DynamicSizeLoader _sizeLoader;
    private readonly ConnectorCycleService _cycleService;
    private readonly PipeConnectSessionContext _ctx;
    private readonly VirtualCtcStore _virtualCtcStore;

    private ITransactionGroupSession? _groupSession;
    private ElementId? _currentFittingId;
    private ConnectorProxy? _activeDynamic;
    private ConnectorProxy? _activeFittingConn2;
    private FittingMappingRule? _activeFittingRule;
    private bool _isClosing;
    private bool _needsPrimaryReducer;
    private ElementId? _primaryReducerId;
    private bool _userManuallyChangedSize;

    private ConnectionGraph? _chainGraph;
    private readonly NetworkSnapshotStore _snapshotStore = new();
    private readonly HashSet<long> _warmedElementIds = [];

    private int _chainDepthField;
    [ObservableProperty] private string _chainDepthHint = "нет цепочки";
    [ObservableProperty] private bool _hasChain;

    public int ChainDepth
    {
        get => _chainDepthField;
        set => SetProperty(ref _chainDepthField, value);
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Инициализация…";
    [ObservableProperty] private FittingCardItem? _selectedFitting;
    [ObservableProperty] private bool _isSessionActive;

    public ObservableCollection<FittingCardItem> AvailableFittings { get; } = [];

    [ObservableProperty] private FittingCardItem? _selectedReducer;
    [ObservableProperty] private bool _isReducerVisible;
    public ObservableCollection<FittingCardItem> AvailableReducers { get; } = [];

    [ObservableProperty] private int _rotationAngleDeg = 15;
    [ObservableProperty] private FamilySizeOption? _selectedDynamicSize;
    [ObservableProperty] private bool _hasSizeOptions;
    [ObservableProperty] private string? _sizeChangeInfo;
    [ObservableProperty] private bool _hasSizeChangeInfo;

    public ObservableCollection<FamilySizeOption> AvailableDynamicSizes { get; } = [];

    public event Action? RequestClose;

    public PipeConnectEditorViewModel(
        PipeConnectSessionContext ctx,
        Document doc,
        ITransactionService txService,
        IConnectorService connSvc,
        ITransformService transformSvc,
        IFittingInsertService fittingInsertSvc,
        IParameterResolver paramResolver,
        IDynamicSizeResolver sizeResolver,
        INetworkMover networkMover,
        IFittingMappingRepository mappingRepo,
        IDialogService dialogSvc,
        IFamilyConnectorService familyConnSvc)
    {
        _ctx = ctx;
        _doc = doc;
        _txService = txService;
        _connSvc = connSvc;
        _transformSvc = transformSvc;
        _fittingInsertSvc = fittingInsertSvc;
        _paramResolver = paramResolver;
        _sizeResolver = sizeResolver;
        _networkMover = networkMover;
        _mappingRepo = mappingRepo;
        _dialogSvc = dialogSvc;
        _familyConnSvc = familyConnSvc;
        _chainOpHandler = new ChainOperationHandler(connSvc, transformSvc, paramResolver, fittingInsertSvc, networkMover);
        _virtualCtcStore = ctx.VirtualCtcStore;
        _ctcManager = new FittingCtcManager(connSvc, mappingRepo, dialogSvc, familyConnSvc, _virtualCtcStore);
        _connectExecutor = new ConnectExecutor(connSvc, transformSvc, paramResolver, fittingInsertSvc, networkMover, mappingRepo, _ctcManager);
        _initHandler = new PipeConnectInitHandler(connSvc, transformSvc, paramResolver, _ctcManager);
        _rotationHandler = new PipeConnectRotationHandler(transformSvc);
        _sizeHandler = new PipeConnectSizeHandler(connSvc, transformSvc, paramResolver, _ctcManager);
        _sizeLoader = new DynamicSizeLoader(connSvc, sizeResolver);
        _cycleService = new ConnectorCycleService(connSvc, transformSvc, paramResolver, _ctcManager);
        _activeDynamic = ctx.DynamicConnector;
        _chainGraph = ctx.ChainGraph;

        var (fittings, reducers) = FittingCardBuilder.Build(
            ctx.ProposedFittings,
            ctx.StaticConnector.ConnectionTypeCode,
            ctx.DynamicConnector.ConnectionTypeCode);

        foreach (var f in fittings) AvailableFittings.Add(f);
        foreach (var r in reducers) AvailableReducers.Add(r);
        SelectedFitting = AvailableFittings.Count > 0 ? AvailableFittings[0] : null;

        LoadDynamicSizes();
        UpdateChainUI();
    }

    private void LoadDynamicSizes()
    {
        var result = _sizeLoader.LoadInitialSizes(_doc, _ctx.DynamicConnector);
        AvailableDynamicSizes.Clear();
        foreach (var s in result.Sizes) AvailableDynamicSizes.Add(s);
        SelectedDynamicSize = result.DefaultSelection;
        HasSizeOptions = result.HasSizeOptions;
    }

    private void RefreshAutoSelectSize()
    {
        var newAuto = _sizeLoader.RefreshAutoSelect(
            _doc, _ctx.DynamicConnector, _activeDynamic!, AvailableDynamicSizes);

        if (newAuto is not null && AvailableDynamicSizes.Count > 0)
        {
            AvailableDynamicSizes[0] = newAuto;
        }

        if (SelectedDynamicSize?.IsAutoSelect == true && AvailableDynamicSizes.Count > 0)
        {
            SelectedDynamicSize = AvailableDynamicSizes[0];
        }
    }

    public void Init()
    {
        SmartConLogger.Info("[Init] START");
        _groupSession = _txService.BeginGroupSession("PipeConnect");
        IsSessionActive = true;

        try
        {
            _activeDynamic = _initHandler.DisconnectAndAlign(_doc, _ctx, _groupSession)
                ?? _ctx.DynamicConnector;

            var conns = GetFreeConnectorsSnapshot();
            _cycleService.State.Initialize(conns, _activeDynamic ?? _ctx.DynamicConnector);
            CycleConnectorCommand.NotifyCanExecuteChanged();

            var defaultFitting = SelectedFitting;
            if (defaultFitting is not null && !defaultFitting.IsDirectConnect)
            {
                StatusMessage = "Установка фитинга…";
                InsertFittingSilent(defaultFitting);
            }
            else if (_ctx.ParamTargetRadius is { } directTargetRadius)
            {
                _activeDynamic = _initHandler.RunDirectConnectSizing(
                    _doc, _ctx, _groupSession, directTargetRadius, AvailableDynamicSizes)
                    ?? _ctx.DynamicConnector;
                StatusMessage = "Готово к соединению";
            }
            else
            {
                StatusMessage = "Готово к соединению";
            }

            if (_currentFittingId is null && _activeDynamic is not null)
            {
                const double radiusEps = 1e-5;
                var dynRadius = _activeDynamic.Radius;
                var staticRadius = _ctx.StaticConnector.Radius;
                if (Math.Abs(dynRadius - staticRadius) > radiusEps)
                {
                    _needsPrimaryReducer = true;
                    SmartConLogger.Info($"[Init] Radii mismatch: dyn={dynRadius * FeetToMm:F1}mm, static={staticRadius * FeetToMm:F1}mm → reducer needed");

                    if (AvailableReducers.Count > 0)
                    {
                        SelectedReducer = AvailableReducers[0];
                        IsReducerVisible = true;
                        StatusMessage = "Вставка переходника…";
                        InsertReducerSilent();
                    }
                }
            }

            RefreshAutoSelectSize();
            SmartConLogger.Info("[Init] DONE");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Init] Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = $"Ошибка инициализации: {ex.Message}";
            _groupSession.RollBack();
            _groupSession = null;
            IsSessionActive = false;
            RequestClose?.Invoke();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RotateLeft() => ExecuteRotate(+RotationAngleDeg);

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void RotateRight() => ExecuteRotate(-RotationAngleDeg);

    private void ExecuteRotate(int angleDeg)
    {
        IsBusy = true;
        try
        {
            _rotationHandler.ExecuteRotation(
                _doc, _groupSession!, _ctx, _activeDynamic,
                _currentFittingId, _primaryReducerId, _chainGraph,
                _snapshotStore, ChainDepth, angleDeg);
            StatusMessage = $"Повёрнуто на {angleDeg:+#;-#;0}°";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Rotate] Failed: {ex.Message}");
            StatusMessage = $"Ошибка поворота: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCycleConnector))]
    private void CycleConnector()
    {
        if (_cycleService.State.Count <= 1) return;

        var target = _cycleService.State.FindNext();
        if (target is null) return;

        IsBusy = true;
        StatusMessage = "Переключение коннектора…";

        try
        {
            var alignTarget = _activeFittingConn2 ?? _ctx.StaticConnector;
            _activeDynamic = _cycleService.CycleAndAlign(
                _doc, _groupSession!, target, alignTarget, _activeDynamic);

            StatusMessage = "Коннектор изменён";
            CycleConnectorCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[CycleConnector] Failed: {ex.Message}");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCycleConnector() => IsSessionActive && !IsBusy && _cycleService.State.Count > 1;

    [RelayCommand(CanExecute = nameof(CanChangeDynamicSize))]
    private void ChangeDynamicSize()
    {
        if (SelectedDynamicSize is null || SelectedDynamicSize.IsAutoSelect) return;

        IsBusy = true;
        StatusMessage = $"Изменение размера на {SelectedDynamicSize.DisplayName}…";
        SmartConLogger.Info($"[ChangeDynamicSize] Attempting size change to {SelectedDynamicSize.DisplayName} " +
            $"(radius={SelectedDynamicSize.Radius * FeetToMm:F2} mm, source={SelectedDynamicSize.Source}, " +
            $"allRadii={SelectedDynamicSize.AllConnectorRadii.Count} коннекторов)");

        try
        {
            var result = _sizeHandler.ChangeSize(
                _doc, _groupSession!, _ctx, SelectedDynamicSize,
                _activeDynamic!, _currentFittingId, _primaryReducerId);

            _activeDynamic = result.ActiveDynamic;
            _userManuallyChangedSize = result.UserManuallyChangedSize;

            var sizeInfo = result.SizeChangeInfo;
            StatusMessage = string.IsNullOrEmpty(sizeInfo)
                ? $"Размер изменён на {SelectedDynamicSize.DisplayName}"
                : $"Размер изменён. {sizeInfo}";

            if (_currentFittingId is not null)
            {
                StatusMessage = "Обновление фитинга…";
                var currentFitting = SelectedFitting;
                if (currentFitting is not null && !currentFitting.IsDirectConnect)
                {
                    SmartConLogger.Info($"[ChangeDynamicSize] Auto-update fitting: {currentFitting.DisplayName}");
                    InsertFittingSilent(currentFitting, adjustDynamicToFit: false);
                }
            }

            if (_primaryReducerId is not null)
            {
                SmartConLogger.Info($"[ChangeDynamicSize] Auto-update reducer (id={_primaryReducerId})");
                var newReducerConn2 = SizeFittingConnectors(_doc, _primaryReducerId, null, adjustDynamicToFit: false);
                if (newReducerConn2 is not null && _activeDynamic is not null)
                {
                    _groupSession!.RunInTransaction("PipeConnect — Позиция dynamic после reducer resize", doc =>
                    {
                        var dynProxy = _connSvc.RefreshConnector(
                            doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                            ?? _activeDynamic;
                        var offset = newReducerConn2.OriginVec3 - dynProxy.OriginVec3;
                        if (!SmartCon.Core.Math.VectorUtils.IsZero(offset))
                            _transformSvc.MoveElement(doc, _activeDynamic.OwnerElementId, offset);
                        doc.Regenerate();
                    });
                }
            }

            if (result.NeedsPrimaryReducer)
            {
                _needsPrimaryReducer = true;

                if (AvailableReducers.Count > 0)
                {
                    SelectedReducer = AvailableReducers[0];
                    IsReducerVisible = true;
                    StatusMessage = "Вставка переходника…";
                    InsertReducerSilent();
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[ChangeDynamicSize] Error: {ex.Message}");
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

    partial void OnSelectedDynamicSizeChanged(FamilySizeOption? value)
    {
        ChangeDynamicSizeCommand.NotifyCanExecuteChanged();

        if (value is null || value.IsAutoSelect)
        {
            SizeChangeInfo = null;
            HasSizeChangeInfo = false;
            return;
        }

        var info = PipeConnectSizeHandler.BuildSizeChangeInfo(value);
        SizeChangeInfo = info;
        HasSizeChangeInfo = !string.IsNullOrEmpty(info);
    }
}
