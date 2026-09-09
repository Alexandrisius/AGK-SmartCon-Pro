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

public sealed partial class PipeConnectEditorViewModel : ObservableObject, IObservableRequestClose, ICloseAwareViewModel
{
    private readonly ITransactionService _txService;
    private readonly Document _doc;
    private readonly IConnectorService _connSvc;
    private readonly ITransformService _transformSvc;
    private readonly IAlignmentService _alignmentSvc;
    private readonly IFittingInsertService _fittingInsertSvc;
    private readonly IParameterResolver _paramResolver;
    private readonly IDynamicSizeResolver _sizeResolver;
    private readonly INetworkMover _networkMover;
    private readonly IFittingMappingRepository _mappingRepo;
    private readonly IDialogService _dialogSvc;
    private readonly IFamilyConnectorService _familyConnSvc;
    private readonly IFittingMapper _fittingMapper;
    private readonly ChainOperationHandler _chainOpHandler;
    private readonly ConnectExecutor _connectExecutor;
    private readonly PipeConnectInitHandler _initHandler;
    private readonly PipeConnectRotationHandler _rotationHandler;
    private readonly PipeConnectSizeHandler _sizeHandler;
    private readonly DynamicSizeLoader _sizeLoader;
    private readonly ConnectorCycleService _cycleService;
    private readonly IViewNavigationService _viewNavigation;
    private readonly PipeConnectSessionContext _ctx;
    private readonly VirtualCtcStore _virtualCtcStore;

    private ITransactionGroupSession? _groupSession;
    private ElementId? _currentFittingId;
    private FittingChainPlan? _activeChainPlan;
    // TODO [ChainV2]: Для поддержки цепочки фитингов (fitting1+fitting2) заменить single _currentFittingId на:
    // private List<ElementId> _fittingChain = []; // ordered: [fitting1, fitting2, ...]
    // Каждый фитинг цепочки подключается к следующему. Reducer подключается к последнему фитингу.
    // См. также TODO в ConnectExecutor.ExecuteConnectTo()
    private ConnectorProxy? _activeDynamic;
    private ConnectorProxy? _activeFittingConn2;

    /// <summary>
    /// Last root dynamic connector chosen by the user via CycleConnector (issue: root
    /// point must follow the cycle selection, not the session-default connector).
    /// Falls back to <see cref="PipeConnectSessionContext.DynamicConnector"/> when the
    /// user never cycled. Used only for OwnerElementId/ConnectorIndex — always refresh
    /// before use (I-05).
    /// </summary>
    private ConnectorProxy? _rootDynamicConnector;

    /// <summary>
    /// Root baseline snapshot (issue #167): full root state (DN/symbol/curve/position)
    /// captured at Init BEFORE any mutation (absorb, resize, ChangeTypeId). The
    /// "Блокировать" toggle restores it via ChainOperationHandler.RestoreElementFromSnapshot.
    /// </summary>
    private ElementSnapshot? _rootBaselineSnapshot;

    /// <summary>
    /// Root compensated snapshot: root state as configured by the session flow
    /// (absorb/подобранный DN), captured when the toggle goes ON — restored on OFF.
    /// </summary>
    private ElementSnapshot? _rootCompensatedSnapshot;

    /// <summary>True when the primary reducer was inserted by the toggle itself
    /// (removed again on OFF). Reducers from the session flow are left alone.</summary>
    private bool _lockInsertedReducer;
    private FittingMappingRule? _activeFittingRule;
    private bool _isClosing;
    private bool _needsPrimaryReducer;
    private ElementId? _primaryReducerId;
    private bool _userManuallyChangedSize;

    private ConnectionGraph? _chainGraph;
    private bool _chainDisabledByCycle;
    private readonly NetworkSnapshotStore _snapshotStore = new();
    private readonly HashSet<long> _warmedElementIds = [];

    private int _chainDepthField;
    [ObservableProperty] private string _chainDepthHint = LocalizationService.GetString("Lbl_NoChain");
    [ObservableProperty] private bool _hasChain;

    public int ChainDepth
    {
        get => _chainDepthField;
        set => SetProperty(ref _chainDepthField, value);
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = LocalizationService.GetString("Status_Initializing");
    [ObservableProperty] private FittingCardItem? _selectedFitting;
    [ObservableProperty] private bool _isSessionActive;

    public ObservableCollection<FittingCardItem> AvailableFittings { get; } = [];

    [ObservableProperty] private FittingCardItem? _selectedReducer;
    [ObservableProperty] private bool _isReducerVisible;
    public ObservableCollection<FittingCardItem> AvailableReducers { get; } = [];

    [ObservableProperty] private int _rotationAngleDeg = 45;
    [ObservableProperty] private FamilySizeOption? _selectedDynamicSize;
    [ObservableProperty] private bool _hasSizeOptions;

    public ObservableCollection<FamilySizeOption> AvailableDynamicSizes { get; } = [];

    public event Action<bool?>? RequestClose;

    public bool IsClosing => _isClosing;

    public PipeConnectEditorViewModel(
        PipeConnectSessionContext ctx,
        Document doc,
        ITransactionService txService,
        IConnectorService connSvc,
        ITransformService transformSvc,
        IAlignmentService alignmentSvc,
        IFittingInsertService fittingInsertSvc,
        IParameterResolver paramResolver,
        IDynamicSizeResolver sizeResolver,
        INetworkMover networkMover,
        IFittingMappingRepository mappingRepo,
        IDialogService dialogSvc,
        IFamilyConnectorService familyConnSvc,
        IFittingMapper fittingMapper,
        ChainOperationHandler chainOpHandler,
        PipeConnectRotationHandler rotationHandler,
        DynamicSizeLoader sizeLoader,
        IViewNavigationService viewNavigation)
    {
        _ctx = ctx;
        _doc = doc;
        _txService = txService;
        _connSvc = connSvc;
        _transformSvc = transformSvc;
        _alignmentSvc = alignmentSvc;
        _fittingInsertSvc = fittingInsertSvc;
        _paramResolver = paramResolver;
        _sizeResolver = sizeResolver;
        _networkMover = networkMover;
        _mappingRepo = mappingRepo;
        _dialogSvc = dialogSvc;
        _familyConnSvc = familyConnSvc;
        _fittingMapper = fittingMapper;
        _chainOpHandler = chainOpHandler;
        _virtualCtcStore = ctx.VirtualCtcStore;
        var resolutionSvc = new CtcResolutionService(connSvc, mappingRepo, _virtualCtcStore);
        var guessSvc = new CtcGuessService(connSvc, mappingRepo, _virtualCtcStore);
        var familyWriter = new CtcFamilyWriter(connSvc, familyConnSvc, _virtualCtcStore);
        _ctcManager = new FittingCtcManager(resolutionSvc, guessSvc, familyWriter);
        _connectExecutor = new ConnectExecutor(connSvc, transformSvc, alignmentSvc, paramResolver, fittingInsertSvc, networkMover, mappingRepo, _ctcManager);
        _initHandler = new PipeConnectInitHandler(connSvc, transformSvc, paramResolver, _ctcManager);
        _rotationHandler = rotationHandler;
        _sizeHandler = new PipeConnectSizeHandler(connSvc, transformSvc, paramResolver, _ctcManager);
        _sizeLoader = sizeLoader;
        _viewNavigation = viewNavigation;
        _cycleService = new ConnectorCycleService(connSvc, alignmentSvc, paramResolver, _ctcManager);
        _activeDynamic = ctx.DynamicConnector;
        _chainGraph = ctx.ChainGraph;
        _elementQueue = _chainGraph?.GetElementQueue();
        _attachedElementIds.Add(ctx.DynamicConnector.OwnerElementId.GetValue());

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
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "RefreshAutoSelectSize"));
        var newAuto = _sizeLoader.RefreshAutoSelect(
            _doc, _activeDynamic ?? _ctx.DynamicConnector, _activeDynamic!, AvailableDynamicSizes);

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
        using var _ = SmartConLogger.Measure(nameof(Init));
        _groupSession = _txService.BeginGroupSession(LocalizationService.GetString("Tx_PipeConnect"));
        IsSessionActive = true;
        _activeChainPlan = _ctx.ChainPlan;

        try
        {
            _activeParentConnector = _ctx.StaticConnector;
            _rootBaselineSnapshot = _chainOpHandler.CaptureSnapshot(
                _doc, _ctx.DynamicConnector.OwnerElementId, _chainGraph);
            _activeDynamic = _initHandler.DisconnectAndAlign(_doc, _ctx, _groupSession)
                ?? _ctx.DynamicConnector;
            _rootDynamicConnector = _activeDynamic;

            var conns = GetFreeConnectorsSnapshot();
            _cycleService.State.Initialize(conns, _activeDynamic ?? _ctx.DynamicConnector);
            CycleConnectorCommand.NotifyCanExecuteChanged();

            if (_activeChainPlan is { Topology: ChainTopology.ReducerFitting })
            {
                SmartConLogger.Info("ReducerFitting topology — inserting reducer first, then fitting");
                InitReducerFittingChain();
            }
            else
            {
                InitLegacyFlow();
            }

            RefreshAutoSelectSize();
            UpdateDynamicInfoPanel();
            TrySealAtCurrentBoundary();
            SmartConLogger.Info("DONE");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_Init"), ex.Message);
            _groupSession.RollBack();
            _groupSession = null;
            IsSessionActive = false;
            RequestClose?.Invoke(null);
        }
    }

}

