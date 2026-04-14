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
using SmartCon.Core;
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

    private List<ConnectorProxy> _allDynamicConnectors = [];
    private readonly HashSet<int> _visitedConnectorIndices = [];
    private int _connectorCyclePos = 0;

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
        _activeDynamic = ctx.DynamicConnector;
        _chainGraph = ctx.ChainGraph;

        bool hasMandatoryFittings = ctx.ProposedFittings.Count > 0 &&
            ctx.ProposedFittings.Any(r => !r.IsDirectConnect && r.FittingFamilies.Count > 0);

        if (!hasMandatoryFittings)
            AvailableFittings.Add(new FittingCardItem(new FittingMappingRule
            {
                FromType = ctx.StaticConnector.ConnectionTypeCode,
                ToType = ctx.DynamicConnector.ConnectionTypeCode,
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

            if (rule.ReducerFamilies.Count > 0)
            {
                foreach (var reducer in rule.ReducerFamilies.OrderBy(f => f.Priority))
                {
                    AvailableReducers.Add(new FittingCardItem(rule, reducer, isReducer: true));
                }
            }
        }

        SelectedFitting = AvailableFittings.Count > 0 ? AvailableFittings[0] : null;

        LoadDynamicSizes();
        UpdateChainUI();
    }

    private void LoadDynamicSizes()
    {
        SmartConLogger.DebugSection("PipeConnectEditorViewModel.LoadDynamicSizes");

        try
        {
            var dynId = _ctx.DynamicConnector.OwnerElementId;
            var dynIdx = _ctx.DynamicConnector.ConnectorIndex;
            SmartConLogger.Debug($"  elementId={dynId.Value}, connIdx={dynIdx}");

            var sizes = _sizeResolver.GetAvailableFamilySizes(_doc, dynId, dynIdx);
            SmartConLogger.Debug($"  GetAvailableFamilySizes returned {sizes.Count} configs");

            var currentRadius = _ctx.DynamicConnector.Radius;
            var currentDn = (int)Math.Round(currentRadius * 2.0 * FeetToMm);
            SmartConLogger.Debug($"  Current size: DN {currentDn} (radius={currentRadius * FeetToMm:F2} mm)");

            var currentConns = _connSvc.GetAllConnectors(_doc, dynId);
            var currentRadii = new Dictionary<int, double>();
            foreach (var c in currentConns)
                currentRadii[c.ConnectorIndex] = c.Radius;

            var queryParamGroups = sizes.Count > 0 ? sizes[0].QueryParamConnectorGroups : [];
            var targetColIdx = sizes.Count > 0 ? sizes[0].TargetColumnIndex : 1;
            var uniqueParamCount = sizes.Count > 0 ? sizes[0].UniqueParameterCount : 1;

            IReadOnlyList<double> autoQueryParamRadii;
            FamilySizeOption? closestOption = sizes.Count > 0
                ? BestSizeMatcher.FindClosestWeighted(sizes, currentRadius, dynIdx, currentRadii)
                : null;

            if (closestOption is not null)
            {
                if (closestOption.QueryParameterRadiiFt.Count > 0)
                {
                    autoQueryParamRadii = closestOption.QueryParameterRadiiFt;
                    targetColIdx = closestOption.TargetColumnIndex;
                    queryParamGroups = closestOption.QueryParamConnectorGroups;
                }
                else
                {
                    autoQueryParamRadii = [closestOption.Radius];
                    targetColIdx = 1;
                    queryParamGroups = [[closestOption.TargetConnectorIndex]];
                }
            }
            else if (queryParamGroups.Count > 0)
            {
                autoQueryParamRadii = BuildCurrentQueryParamRadii(currentRadii, queryParamGroups);
            }
            else
            {
                autoQueryParamRadii = [currentRadii.GetValueOrDefault(dynIdx, 0)];
                targetColIdx = 1;
            }

            var autoDisplayName = FamilySizeFormatter.BuildAutoSelectDisplayName(autoQueryParamRadii, targetColIdx);

            AvailableDynamicSizes.Add(new FamilySizeOption
            {
                DisplayName = autoDisplayName,
                Radius = currentRadius,
                TargetConnectorIndex = dynIdx,
                AllConnectorRadii = currentRadii,
                QueryParameterRadiiFt = autoQueryParamRadii,
                UniqueParameterCount = uniqueParamCount,
                TargetColumnIndex = targetColIdx,
                QueryParamConnectorGroups = queryParamGroups,
                Source = "",
                IsAutoSelect = true
            });

            foreach (var size in sizes)
            {
                if (!AvailableDynamicSizes.Any(s => s.DisplayName == size.DisplayName))
                {
                    AvailableDynamicSizes.Add(size with { IsAutoSelect = false });
                    SmartConLogger.Debug($"  Added: {size.DisplayName}");
                }
            }

            SelectedDynamicSize = AvailableDynamicSizes.Count > 0 ? AvailableDynamicSizes[0] : null;
            HasSizeOptions = AvailableDynamicSizes.Count > 1;
            SmartConLogger.Debug($"  Total: {AvailableDynamicSizes.Count} options, HasSizeOptions={HasSizeOptions}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[LoadDynamicSizes] Error: {ex.Message}");
            HasSizeOptions = false;
        }
    }

    private static IReadOnlyList<double> BuildCurrentQueryParamRadii(
        IReadOnlyDictionary<int, double> currentRadii,
        IReadOnlyList<IReadOnlyList<int>> queryParamGroups)
    {
        var result = new List<double>(queryParamGroups.Count);
        foreach (var group in queryParamGroups)
        {
            double radius = 0;
            foreach (var ci in group)
            {
                if (currentRadii.TryGetValue(ci, out var r))
                {
                    radius = r;
                    break;
                }
            }
            result.Add(radius);
        }
        return result.AsReadOnly();
    }

    private void RefreshAutoSelectSize()
    {
        if (_activeDynamic is null) return;

        var dynId = _ctx.DynamicConnector.OwnerElementId;
        var currentConns = _connSvc.GetAllConnectors(_doc, dynId);
        var currentRadii = new Dictionary<int, double>();
        foreach (var c in currentConns)
            currentRadii[c.ConnectorIndex] = c.Radius;

        var autoOption = AvailableDynamicSizes.FirstOrDefault(s => s.IsAutoSelect);
        var queryParamGroups = autoOption?.QueryParamConnectorGroups ?? [];
        var targetColIdx = autoOption?.TargetColumnIndex ?? 1;

        IReadOnlyList<double> autoQueryParamRadii;
        var nonAutoSizes = AvailableDynamicSizes.Where(s => !s.IsAutoSelect).ToList();
        if (nonAutoSizes.Count > 0)
        {
            var dynIdx = _ctx.DynamicConnector.ConnectorIndex;
            var targetRadius = currentRadii.GetValueOrDefault(dynIdx, 0);
            var best = BestSizeMatcher.FindClosestWeighted(nonAutoSizes, targetRadius, dynIdx, currentRadii);
            if (best is not null)
            {
                if (best.QueryParameterRadiiFt.Count > 0)
                {
                    autoQueryParamRadii = best.QueryParameterRadiiFt;
                    queryParamGroups = best.QueryParamConnectorGroups;
                    targetColIdx = best.TargetColumnIndex;
                }
                else
                {
                    autoQueryParamRadii = [best.Radius];
                    targetColIdx = 1;
                    queryParamGroups = [[best.TargetConnectorIndex]];
                }
            }
            else if (queryParamGroups.Count > 0)
                autoQueryParamRadii = BuildCurrentQueryParamRadii(currentRadii, queryParamGroups);
            else
                autoQueryParamRadii = [currentRadii.GetValueOrDefault(dynIdx, 0)];
        }
        else if (queryParamGroups.Count > 0)
            autoQueryParamRadii = BuildCurrentQueryParamRadii(currentRadii, queryParamGroups);
        else
            autoQueryParamRadii = [currentRadii.GetValueOrDefault(_ctx.DynamicConnector.ConnectorIndex, 0)];

        var autoDisplayName = FamilySizeFormatter.BuildAutoSelectDisplayName(autoQueryParamRadii, targetColIdx);

        if (AvailableDynamicSizes.Count > 0)
        {
            var newAutoOption = new FamilySizeOption
            {
                DisplayName = autoDisplayName,
                Radius = _activeDynamic.Radius,
                TargetConnectorIndex = _ctx.DynamicConnector.ConnectorIndex,
                AllConnectorRadii = currentRadii,
                QueryParameterRadiiFt = autoQueryParamRadii,
                UniqueParameterCount = autoOption?.UniqueParameterCount ?? 1,
                TargetColumnIndex = targetColIdx,
                QueryParamConnectorGroups = queryParamGroups,
                Source = "",
                IsAutoSelect = true
            };
            AvailableDynamicSizes[0] = newAutoOption;
            SmartConLogger.Debug($"[RefreshAutoSelectSize] Updated: {autoDisplayName}");
        }

        if (SelectedDynamicSize?.IsAutoSelect == true)
        {
            SelectedDynamicSize = AvailableDynamicSizes[0];
        }
    }

    // ── Init ─────────────────────────────────────────────────────────────────

    public void Init()
    {
        _groupSession = _txService.BeginGroupSession("PipeConnect");
        IsSessionActive = true;

        try
        {
            _activeDynamic = _initHandler.DisconnectAndAlign(_doc, _ctx, _groupSession)
                ?? _ctx.DynamicConnector;

            var conns = GetFreeConnectorsSnapshot();
            InitConnectorCycle(conns);

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

    // ── Rotation ────────────────────────────────────────────────────────────

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
            StatusMessage = $"Ошибка поворота: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Cycle connector ─────────────────────────────────────────────────────

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
                _activeDynamic = RefreshWithCtcOverride(doc, dynId, target.ConnectorIndex);
            });

            _visitedConnectorIndices.Add(target.ConnectorIndex);
            StatusMessage = "Коннектор изменён";
            CycleConnectorCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
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
            int idx = (_connectorCyclePos + i) % count;
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

    // ── Change dynamic size ─────────────────────────────────────────────────

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
                        if (!VectorUtils.IsZero(offset))
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
