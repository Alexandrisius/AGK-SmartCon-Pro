using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Holds the result of loading dynamic sizes for a family element.
/// </summary>
public sealed class DynamicSizeLoadResult
{
    public required List<FamilySizeOption> Sizes { get; init; }
    public required FamilySizeOption? DefaultSelection { get; init; }
    public required bool HasSizeOptions { get; init; }
}

/// <summary>
/// Loads available dynamic sizes for family elements during PipeConnect sessions.
/// </summary>
public sealed class DynamicSizeLoader(
    IConnectorService connSvc,
    IDynamicSizeResolver sizeResolver)
{
    public DynamicSizeLoadResult LoadInitialSizes(
        Document doc,
        ConnectorProxy dynamicConn)
    {
        SmartConLogger.DebugSection("DynamicSizeLoader.LoadInitialSizes");

        var sizes = new List<FamilySizeOption>();
        FamilySizeOption? defaultSelection = null;
        bool hasSizeOptions = false;

        try
        {
            var dynId = dynamicConn.OwnerElementId;
            var dynIdx = dynamicConn.ConnectorIndex;
            SmartConLogger.Debug($"  elementId={dynId.GetValue()}, connIdx={dynIdx}");

            var availableConfigs = sizeResolver.GetAvailableFamilySizes(doc, dynId, dynIdx);
            SmartConLogger.Debug($"  GetAvailableFamilySizes returned {availableConfigs.Count} configs");

            var currentRadius = dynamicConn.Radius;
            var currentDn = (int)Math.Round(currentRadius * 2.0 * FeetToMm);
            SmartConLogger.Debug($"  Current size: DN {currentDn} (radius={currentRadius * FeetToMm:F2} mm)");

            var currentConns = connSvc.GetAllConnectors(doc, dynId);
            var currentRadii = new Dictionary<int, double>();
            foreach (var c in currentConns)
                currentRadii[c.ConnectorIndex] = c.Radius;

            var queryParamGroups = availableConfigs.Count > 0 ? availableConfigs[0].QueryParamConnectorGroups : [];
            var targetColIdx = availableConfigs.Count > 0 ? availableConfigs[0].TargetColumnIndex : 1;
            var uniqueParamCount = availableConfigs.Count > 0 ? availableConfigs[0].UniqueParameterCount : 1;

            IReadOnlyList<double> autoQueryParamRadii;
            FamilySizeOption? closestOption = availableConfigs.Count > 0
                ? BestSizeMatcher.FindClosestWeighted(availableConfigs, currentRadius, dynIdx, currentRadii)
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

            sizes.Add(new FamilySizeOption
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

            foreach (var size in availableConfigs)
            {
                if (!sizes.Any(s => s.DisplayName == size.DisplayName))
                {
                    sizes.Add(size with { IsAutoSelect = false });
                    SmartConLogger.Debug($"  Added: {size.DisplayName}");
                }
            }

            defaultSelection = sizes.Count > 0 ? sizes[0] : null;
            hasSizeOptions = sizes.Count > 1;
            SmartConLogger.Debug($"  Total: {sizes.Count} options, HasSizeOptions={hasSizeOptions}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[LoadInitialSizes] Error: {ex.Message}");
            hasSizeOptions = false;
        }

        return new DynamicSizeLoadResult
        {
            Sizes = sizes,
            DefaultSelection = defaultSelection,
            HasSizeOptions = hasSizeOptions
        };
    }

    public FamilySizeOption? RefreshAutoSelect(
        Document doc,
        ConnectorProxy dynamicConn,
        ConnectorProxy activeDynamic,
        IReadOnlyList<FamilySizeOption> currentOptions)
    {
        if (activeDynamic is null) return null;

        var dynId = dynamicConn.OwnerElementId;
        var currentConns = connSvc.GetAllConnectors(doc, dynId);
        var currentRadii = new Dictionary<int, double>();
        foreach (var c in currentConns)
            currentRadii[c.ConnectorIndex] = c.Radius;

        var autoOption = currentOptions.FirstOrDefault(s => s.IsAutoSelect);
        var queryParamGroups = autoOption?.QueryParamConnectorGroups ?? [];
        var targetColIdx = autoOption?.TargetColumnIndex ?? 1;

        IReadOnlyList<double> autoQueryParamRadii;
        var nonAutoSizes = currentOptions.Where(s => !s.IsAutoSelect).ToList();
        if (nonAutoSizes.Count > 0)
        {
            var dynIdx = dynamicConn.ConnectorIndex;
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
            autoQueryParamRadii = [currentRadii.GetValueOrDefault(dynamicConn.ConnectorIndex, 0)];

        var autoDisplayName = FamilySizeFormatter.BuildAutoSelectDisplayName(autoQueryParamRadii, targetColIdx);

        var newAutoOption = new FamilySizeOption
        {
            DisplayName = autoDisplayName,
            Radius = activeDynamic.Radius,
            TargetConnectorIndex = dynamicConn.ConnectorIndex,
            AllConnectorRadii = currentRadii,
            QueryParameterRadiiFt = autoQueryParamRadii,
            UniqueParameterCount = autoOption?.UniqueParameterCount ?? 1,
            TargetColumnIndex = targetColIdx,
            QueryParamConnectorGroups = queryParamGroups,
            Source = "",
            IsAutoSelect = true
        };

        SmartConLogger.Debug($"[RefreshAutoSelectSize] Updated: {autoDisplayName}");
        return newAutoOption;
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
}
