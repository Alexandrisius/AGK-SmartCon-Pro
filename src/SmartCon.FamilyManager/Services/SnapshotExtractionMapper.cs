using System.Globalization;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Maps in-memory <see cref="FamilySnapshot"/> / <see cref="SystemFamilySnapshot"/>
/// (produced in Phase 1 Prepare) to the <see cref="FamilyExtractionResult"/> and
/// <see cref="FamilyTypeDescriptor"/> shapes that the catalog persistence layer
/// (<see cref="IFamilyDataImportService.SaveExtractionResultAsync"/>,
/// <see cref="IFamilyTypeRepository.SyncTypesAsync"/>) expects.
/// <para>
/// This eliminates the Phase 3 Commit re-opening of managed .rfa / staged .rvt
/// files: the snapshot already contains every type name, parameter value, and
/// shared-nested family name that <c>RevitFamilyDataExtractionService.ExtractFromManagedFile</c>
/// and <c>LoadableFamilyTypeResolver.ResolveTypesFromRfa</c> would extract via a
/// second <c>OpenDocumentFile</c> call.
/// </para>
/// <para>
/// Pure C# — no Revit API calls. Safe to invoke from any thread.
/// </para>
/// </summary>
internal static class SnapshotExtractionMapper
{
    /// <summary>
    /// Maps a loadable <see cref="FamilySnapshot"/> to a
    /// <see cref="FamilyExtractionResult"/> for <c>SaveExtractionResultAsync</c>.
    /// Types are sorted by name (Ordinal) and assigned sequential SortOrder,
    /// mirroring <c>RevitFamilyDataExtractionService.ExtractCore</c>.
    /// </summary>
    public static FamilyExtractionResult ToExtractionResult(
        FamilySnapshot snapshot,
        int revitMajorVersion)
    {
        if (snapshot is null)
            return new FamilyExtractionResult(false, [], null, "Snapshot is null", revitMajorVersion, []);

        using var _scope = SmartConLogger.BeginScope("SnapshotMap",
            ("Method", nameof(ToExtractionResult) + ":Loadable"),
            ("Family", snapshot.FamilyName),
            ("TypeCount", snapshot.Types.Count),
            ("ParamCount", snapshot.Parameters.Count),
            ("SharedNested", snapshot.SharedNestedFamilyNames.Count));

        // Build ParameterName → IsInstance lookup so each value gets the
        // correct AttributeScope (Type vs Instance). Mirrors the param.IsInstance
        // check in RevitFamilyDataExtractionService.ExtractValueForParameter.
        var scopeByName = new Dictionary<string, AttributeScope>(StringComparer.Ordinal);
        foreach (var p in snapshot.Parameters)
        {
            if (!string.IsNullOrEmpty(p.Name))
                scopeByName[p.Name] = p.IsInstance ? AttributeScope.Instance : AttributeScope.Type;
        }

        var types = new List<FamilyExtractionTypeValues>(snapshot.Types.Count);
        var sortOrder = 0;
        foreach (var t in snapshot.Types
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var values = new List<FamilyExtractionValueResult>(t.Values.Count);
            foreach (var v in t.Values)
            {
                values.Add(ToValueResult(v, scopeByName));
            }
            types.Add(new FamilyExtractionTypeValues(t.Name, sortOrder++, values));
        }

        var sharedNested = snapshot.SharedNestedFamilyNames;

        SmartConLogger.Info(
            $"Mapped loadable snapshot → extraction result: {types.Count} types, " +
            $"{types.Sum(t => t.Values.Count)} values, {sharedNested.Count} shared nested " +
            $"(from snapshot, no re-open)");

        foreach (var t in types)
        {
            var found = t.Values.Count(v => v.Status == AttributeValueStatus.Found);
            var empty = t.Values.Count(v => v.Status == AttributeValueStatus.EmptyValue);
            SmartConLogger.Debug(
                $"  Type '{t.TypeName}' (sort={t.SortOrder}): {t.Values.Count} values " +
                $"({found} found, {empty} empty), UniqueId={snapshot.Types.FirstOrDefault(x => x.Name == t.TypeName)?.UniqueId ?? "<null>"}");
        }

        return new FamilyExtractionResult(
            Success: true,
            Types: types,
            UntypedValues: null,
            ErrorMessage: null,
            RevitMajorVersion: revitMajorVersion,
            SharedNestedFamilyNames: sharedNested);
    }

    /// <summary>
    /// Maps a system <see cref="SystemFamilySnapshot"/> to a
    /// <see cref="FamilyExtractionResult"/> for <c>SaveExtractionResultAsync</c>.
    /// All parameter values are <see cref="AttributeScope.Type"/> (system family
    /// types have no instance parameters in the snapshot).
    /// </summary>
    public static FamilyExtractionResult ToExtractionResult(
        SystemFamilySnapshot snapshot,
        int revitMajorVersion)
    {
        if (snapshot is null)
            return new FamilyExtractionResult(false, [], null, "System snapshot is null", revitMajorVersion, []);

        using var _scope = SmartConLogger.BeginScope("SnapshotMap",
            ("Method", nameof(ToExtractionResult) + ":System"),
            ("Category", snapshot.CategoryName),
            ("TypeCount", snapshot.Types.Count));

        var types = new List<FamilyExtractionTypeValues>(snapshot.Types.Count);
        var sortOrder = 0;
        foreach (var t in snapshot.Types
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var values = new List<FamilyExtractionValueResult>(t.Values.Count);
            foreach (var v in t.Values)
            {
                // System family types only carry Type-scope parameters.
                values.Add(ToValueResult(v, null, AttributeScope.Type));
            }
            types.Add(new FamilyExtractionTypeValues(t.Name, sortOrder++, values));
        }

        SmartConLogger.Info(
            $"Mapped system snapshot → extraction result: {types.Count} types, " +
            $"{types.Sum(t => t.Values.Count)} values (from snapshot, no re-open)");

        foreach (var t in types)
        {
            var found = t.Values.Count(v => v.Status == AttributeValueStatus.Found);
            var empty = t.Values.Count(v => v.Status == AttributeValueStatus.EmptyValue);
            SmartConLogger.Debug(
                $"  System type '{t.TypeName}' (sort={t.SortOrder}): {t.Values.Count} values " +
                $"({found} found, {empty} empty)");
        }

        return new FamilyExtractionResult(
            Success: true,
            Types: types,
            UntypedValues: null,
            ErrorMessage: null,
            RevitMajorVersion: revitMajorVersion,
            SharedNestedFamilyNames: []);
    }

    /// <summary>
    /// Maps a loadable <see cref="FamilySnapshot"/> to
    /// <see cref="FamilyTypeDescriptor"/> rows for <c>SyncTypesAsync</c>.
    /// Replaces <c>LoadableFamilyTypeResolver.ResolveTypesFromRfa</c> — the
    /// UniqueId is taken from <see cref="FamilyTypeSnapshot.UniqueId"/> which
    /// the snapshot extractor collected from the FamilySymbol in the single
    /// Prepare open, eliminating the 42× LoadableResolver.OpenDocumentFile
    /// calls in the post-import flow.
    /// </summary>
    public static IReadOnlyList<FamilyTypeDescriptor> ToTypeDescriptors(
        FamilySnapshot snapshot,
        string catalogItemId,
        string? versionId = null,
        string? fileId = null)
    {
        if (snapshot is null) return [];

        var result = new List<FamilyTypeDescriptor>(snapshot.Types.Count);
        var sortOrder = 0;
        foreach (var t in snapshot.Types
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            result.Add(new FamilyTypeDescriptor(
                Id: Guid.NewGuid().ToString(),
                CatalogItemId: catalogItemId,
                Name: t.Name,
                SortOrder: sortOrder++,
                VersionId: versionId,
                FileId: fileId,
                ExtractionRunId: null,
                UniqueId: t.UniqueId));
        }

        using var _scope = SmartConLogger.BeginScope("SnapshotMap",
            ("Method", nameof(ToTypeDescriptors)),
            ("Family", snapshot.FamilyName),
            ("TypeCount", result.Count));
        SmartConLogger.Info(
            $"Mapped loadable snapshot → {result.Count} type descriptor(s) " +
            $"(from snapshot, no re-open, UniqueId populated={result.Count(r => r.UniqueId is not null)}/{result.Count})");

        return result;
    }

    private static FamilyExtractionValueResult ToValueResult(
        FamilyParameterValue v,
        Dictionary<string, AttributeScope>? scopeByName)
    {
        AttributeScope? scope = null;
        if (scopeByName is not null && scopeByName.TryGetValue(v.ParameterName, out var s))
            scope = s;
        return ToValueResultCore(v.ParameterName, v.StorageType, v.HasValue, v.ValueText, v.ValueNumber, scope,
            v.ValueDisplay, v.UnitTypeId);
    }

    private static FamilyExtractionValueResult ToValueResult(
        SystemParameterValue v,
        Dictionary<string, AttributeScope>? scopeByName,
        AttributeScope? fixedScope)
    {
        var scope = fixedScope;
        if (scope is null && scopeByName is not null && scopeByName.TryGetValue(v.ParameterName, out var s))
            scope = s;
        return ToValueResultCore(v.ParameterName, v.StorageType, v.HasValue, v.ValueText, v.ValueNumber, scope,
            v.ValueDisplay, v.UnitTypeId);
    }

    private static FamilyExtractionValueResult ToValueResultCore(
        string parameterName,
        string storageType,
        bool hasValue,
        string? valueText,
        double? valueNumber,
        AttributeScope? scope,
        string? valueDisplay,
        string? unitTypeId)
    {
        if (!hasValue)
        {
            return new FamilyExtractionValueResult(
                parameterName,
                scope,
                storageType,
                ValueText: null,
                ValueRaw: null,
                ValueNumber: null,
                UnitTypeId: null,
                Status: AttributeValueStatus.EmptyValue,
                Message: "Parameter has no value");
        }

        // HasValue = true — build ValueRaw to mirror ExtractValueForParameter.
        var valueRaw = ComputeValueRaw(valueNumber, valueText);

        return new FamilyExtractionValueResult(
            parameterName,
            scope,
            storageType,
            ValueText: valueDisplay ?? valueText,
            ValueRaw: valueRaw,
            ValueNumber: valueNumber,
            UnitTypeId: unitTypeId,
            Status: AttributeValueStatus.Found,
            Message: null);
    }

    /// <summary>
    /// Resolves the type count to display in the batch import dialog from
    /// the in-memory snapshots produced in Phase 1 Prepare. Priority:
    /// <paramref name="sourceTypes"/> (system families — selected/placed
    /// types count) → <paramref name="loadableSnapshot"/> (loadable
    /// families — named types from <c>FamilyManager.Types</c>) →
    /// <paramref name="systemSnapshot"/> (system families — all category
    /// types, fallback when <paramref name="sourceTypes"/> is null) →
    /// <c>null</c> (Prepare failed or no snapshot — the dialog shows "—").
    /// <para>
    /// This ensures the "Types" column is populated for every use case
    /// (UC-1 file import, UC-2 active .rfa, UC-3 active project, UC-4
    /// selected elements) without re-opening any document — the snapshot
    /// already carries the count from the single Prepare open.
    /// </para>
    /// </summary>
    public static int? ResolveTypeCount(
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        IReadOnlyList<FamilySourceTypeInfo>? sourceTypes)
    {
        if (sourceTypes is not null) return sourceTypes.Count;
        if (loadableSnapshot?.Types is not null) return loadableSnapshot.Types.Count;
        if (systemSnapshot?.Types is not null) return systemSnapshot.Types.Count;
        return null;
    }

    /// <summary>
    /// Resolves the display-ready type-name list for the batch import
    /// dialog's Types-column tooltip. Same source priority as
    /// <see cref="ResolveTypeCount"/> (sourceTypes → loadableSnapshot →
    /// systemSnapshot → <c>null</c>), so the tooltip always matches the
    /// number shown in the column. The synthetic
    /// <see cref="FamilyTypeSnapshot.DefaultTypeName"/> literal is replaced
    /// with <paramref name="familyName"/> via
    /// <see cref="FamilyTypeSnapshot.ResolveDisplayName"/> — the user never
    /// sees the raw "&lt;default&gt;" marker.
    /// </summary>
    public static IReadOnlyList<string>? ResolveTypeNames(
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        IReadOnlyList<FamilySourceTypeInfo>? sourceTypes,
        string familyName)
    {
        if (sourceTypes is not null)
            return sourceTypes.Select(t => FamilyTypeSnapshot.ResolveDisplayName(t.Name, familyName)).ToList();
        if (loadableSnapshot?.Types is not null)
            return loadableSnapshot.Types.Select(t => FamilyTypeSnapshot.ResolveDisplayName(t.Name, familyName)).ToList();
        if (systemSnapshot?.Types is not null)
            return systemSnapshot.Types.Select(t => FamilyTypeSnapshot.ResolveDisplayName(t.Name, familyName)).ToList();
        return null;
    }

    /// <summary>
    /// Computes a best-effort ValueRaw string matching the convention of
    /// <c>RevitFamilyDataExtractionService.ExtractValueForParameter</c>:
    /// Double → invariant full-precision, Integer → ToString, String → value,
    /// ElementId → resolved name or id string (snapshot carries the name).
    /// </summary>
    private static string? ComputeValueRaw(double? valueNumber, string? valueText)
    {
        if (valueNumber.HasValue)
            return FormattableString.Invariant($"{valueNumber.Value}");
        return valueText;
    }
}
