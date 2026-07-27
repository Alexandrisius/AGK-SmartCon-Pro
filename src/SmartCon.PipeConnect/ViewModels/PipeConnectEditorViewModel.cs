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
    /// Root absorb undo data (issue #165): captured at Init when the root dynamic is
    /// a pipe whose initial alignment was absorbed into its geometry (ADR-052).
    /// The "Блокировать" toggle reverts the absorb and applies the rigid move instead.
    /// </summary>
    private bool _rootAbsorbApplied;
    private XYZ? _rootPipeStart;
    private XYZ? _rootPipeEnd;
    private IReadOnlyList<XYZ>? _rootFlexPoints;
    private Vec3 _rootRigidOffset;

    /// <summary>True after the toggle replaced the root absorb with a rigid move.</summary>
    private bool _rootRigidApplied;
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
        DynamicSizeLoader sizeLoader)
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
            var initOutcome = _initHandler.DisconnectAndAlign(_doc, _ctx, _groupSession);
            _activeDynamic = initOutcome.ActiveDynamic ?? _ctx.DynamicConnector;
            _rootDynamicConnector = _activeDynamic;
            _rootAbsorbApplied = initOutcome.AbsorbApplied;
            _rootPipeStart = initOutcome.PipeStart;
            _rootPipeEnd = initOutcome.PipeEnd;
            _rootFlexPoints = initOutcome.FlexPoints;
            _rootRigidOffset = initOutcome.RigidOffset;

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

    private void InitLegacyFlow()
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "InitLegacyFlow"));
        var defaultFitting = SelectedFitting;
        if (defaultFitting is not null && !defaultFitting.IsDirectConnect)
        {
            StatusMessage = LocalizationService.GetString("Status_InsertingFitting");
            InsertFittingSilent(defaultFitting);
        }
        else if (_ctx.ParamTargetRadius is { } directTargetRadius)
        {
            _activeDynamic = _initHandler.RunDirectConnectSizing(
                _doc, _ctx, _groupSession!, directTargetRadius, AvailableDynamicSizes)
                ?? _ctx.DynamicConnector;
            StatusMessage = LocalizationService.GetString("Status_ReadyToConnect");
        }
        else
        {
            StatusMessage = LocalizationService.GetString("Status_ReadyToConnect");
        }

        if (_primaryReducerId is null && _activeDynamic is not null)
        {
            bool needsReducer;

            if (_currentFittingId is not null && _activeFittingConn2 is not null)
            {
                needsReducer = PipeConnectSizeHandler.DetectReducerNeededAfterFitting(
                    _activeDynamic, _activeFittingConn2);
            }
            else if (_currentFittingId is null)
            {
                const double radiusEps = 1e-5;
                var dynRadius = _activeDynamic.Radius;
                var staticRadius = _ctx.StaticConnector.Radius;
                needsReducer = Math.Abs(dynRadius - staticRadius) > radiusEps;

                if (needsReducer)
                    SmartConLogger.Info($"Radii mismatch: dyn={dynRadius * FeetToMm:F1}mm, " +
                        $"static={staticRadius * FeetToMm:F1}mm → reducer needed");
            }
            else
            {
                needsReducer = false;
            }

            if (needsReducer)
            {
                _needsPrimaryReducer = true;

                if (AvailableReducers.Count > 0)
                {
                    SelectedReducer = AvailableReducers[0];
                    IsReducerVisible = true;
                    StatusMessage = LocalizationService.GetString("Status_InsertingReducer");
                    InsertReducerSilent();
                }
            }
        }
    }

    private void InitReducerFittingChain()
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "InitReducerFittingChain"));
        // TODO [ChainV2]: Обобщить для N звеньев. Сейчас работает для 2 звеньев: reducer + fitting.
        var plan = _activeChainPlan!;

        if (plan.Links.Count < 2)
        {
            SmartConLogger.Warn("ReducerFitting plan has < 2 links — falling back to legacy flow " +
                "[Action: если соединение собрано не так, как ожидалось, сообщите разработчикам — план цепочки фитингов некорректен]");
            InitLegacyFlow();
            return;
        }

        var reducerLink = plan.Links[0];
        var fittingLink = plan.Links[1];

        if (reducerLink.Type != FittingChainNodeType.Reducer ||
            fittingLink.Type != FittingChainNodeType.Fitting)
        {
            SmartConLogger.Warn("ReducerFitting plan has unexpected link types — falling back to legacy flow " +
                "[Action: если соединение собрано не так, как ожидалось, сообщите разработчикам — типы звеньев плана некорректны]");
            InitLegacyFlow();
            return;
        }

        _activeFittingRule = fittingLink.Rule;

        // Step 1: Insert REDUCER aligned to static
        ElementId? insertedReducerId = null;
        ConnectorProxy? reducerConn2 = null;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertReducer"), doc =>
        {
            insertedReducerId = _fittingInsertSvc.InsertFitting(
                doc, reducerLink.Family.FamilyName, reducerLink.Family.SymbolName,
                _ctx.StaticConnector.Origin);

            if (insertedReducerId is null) return;

            SmartConLogger.Info($"ReducerFitting: inserted reducer id={insertedReducerId.GetValue()}");
            doc.Regenerate();

            var overrides = GuessCtcForReducer(insertedReducerId);

            reducerConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedReducerId, _ctx.StaticConnector, _transformSvc, _connSvc,
                dynamicTypeCode: reducerLink.CtcOut,
                ctcOverrides: overrides,
                directConnectRules: _mappingRepo.GetMappingRules());

            doc.Regenerate();
        });

        if (insertedReducerId is null)
        {
            SmartConLogger.Warn("ReducerFitting: reducer insertion failed — falling back " +
                "[Action: проверьте, что семейство переходника загружено в проект и mapping указывает на существующий тип]");
            InitLegacyFlow();
            return;
        }

        _primaryReducerId = insertedReducerId;
        SizeFittingConnectors(_doc, insertedReducerId, reducerConn2, adjustDynamicToFit: false);

        // Refresh reducer conn2 after sizing
        var allRConns = _connSvc.GetAllFreeConnectors(_doc, insertedReducerId).ToList();
        reducerConn2 = allRConns.Count >= 2 ? allRConns[1] : allRConns.FirstOrDefault();

        // Step 2: Insert FITTING aligned to reducer.conn2
        var fittingFamily = fittingLink.Family;
        ElementId? insertedFittingId = null;
        ConnectorProxy? fitConn2 = null;
        ConnectorProxy? alignTarget = reducerConn2 ?? _ctx.StaticConnector;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertFitting"), doc =>
        {
            insertedFittingId = _fittingInsertSvc.InsertFitting(
                doc, fittingFamily.FamilyName, fittingFamily.SymbolName,
                alignTarget.Origin);

            if (insertedFittingId is null) return;

            SmartConLogger.Info($"ReducerFitting: inserted fitting id={insertedFittingId.GetValue()}");
            doc.Regenerate();

            var ctcOverrides = GuessCtcForFitting(insertedFittingId, fittingLink.Rule);
            var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedFittingId, alignTarget, _transformSvc, _connSvc,
                dynamicTypeCode: dynCtc,
                ctcOverrides: ctcOverrides,
                directConnectRules: _mappingRepo.GetMappingRules());

            if (fitConn2 is not null && _activeDynamic is not null)
            {
                var activeProxy = _connSvc.RefreshConnector(
                    doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                    ?? _activeDynamic;
                var offset = fitConn2.OriginVec3 - activeProxy.OriginVec3;
                if (!VectorUtils.IsZero(offset))
                    PipeAbsorptionApplier.MoveOrAbsorb(
                        doc, _transformSvc, _activeDynamic.OwnerElementId, activeProxy.OriginVec3, offset);
            }

            doc.Regenerate();
        });

        if (insertedFittingId is not null)
        {
            _currentFittingId = insertedFittingId;
            _activeFittingConn2 = fitConn2;
            StatusMessage = string.Format(LocalizationService.GetString("Status_Inserted"), fittingFamily.FamilyName);

            var newFitConn2 = SizeFittingConnectors(_doc, insertedFittingId, fitConn2);
            if (newFitConn2 is not null)
                _activeFittingConn2 = newFitConn2;
        }

        _needsPrimaryReducer = true;
        IsReducerVisible = true;
        SmartConLogger.Info($"ReducerFitting: DONE reducer={_primaryReducerId?.GetValue()}, fitting={_currentFittingId?.GetValue()}");
    }

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

    private void EnsureReducersForFittingPair(ConnectorProxy fitConn2, ConnectorProxy dynamicConn)
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "EnsureReducersForFittingPair"));
        if (AvailableReducers.Count > 0) return;

        var fitCtc = fitConn2.ConnectionTypeCode.IsDefined
            ? fitConn2.ConnectionTypeCode
            : new ConnectionTypeCode(0);
        var dynCtc = dynamicConn.ConnectionTypeCode.IsDefined
            ? dynamicConn.ConnectionTypeCode
            : new ConnectionTypeCode(0);

        if (!fitCtc.IsDefined || !dynCtc.IsDefined) return;

        var rules = _mappingRepo.GetMappingRules();

        foreach (var rule in rules)
        {
            if (rule.ReducerFamilies.Count == 0) continue;

            bool match = (rule.FromType.Value == fitCtc.Value && rule.ToType.Value == dynCtc.Value) ||
                         (rule.FromType.Value == dynCtc.Value && rule.ToType.Value == fitCtc.Value);

            if (match)
            {
                SmartConLogger.Info($"Found reducer rule: From={rule.FromType.Value} To={rule.ToType.Value} ({rule.ReducerFamilies.Count} families)");
                foreach (var reducer in rule.ReducerFamilies.OrderBy(f => f.Priority))
                    AvailableReducers.Add(new FittingCardItem(rule, reducer, isReducer: true));
                return;
            }
        }

        SmartConLogger.Info($"No reducer rule found for pair CTC {fitCtc.Value} ↔ {dynCtc.Value}");
    }
}

