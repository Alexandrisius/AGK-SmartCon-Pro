using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="ISystemTypeSyncService"/> (Issue #104).
/// Never copies elements between documents: the reference data of the source
/// type (snapshot) is read from the open mini-project and written into the
/// target type of the active project. Missing types are created by
/// duplicating an existing prototype type of the same category.
/// </summary>
public sealed class SystemTypeSyncService : ISystemTypeSyncService
{
    private readonly ITransactionService _tx;
    private readonly IFamilySnapshotExtractor _snapshotExtractor;
    private readonly ISystemTypeFinder _typeFinder;
    private readonly IClock _clock;

    public SystemTypeSyncService(
        ITransactionService tx,
        IFamilySnapshotExtractor snapshotExtractor,
        ISystemTypeFinder typeFinder,
        IClock clock)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(snapshotExtractor);
        ArgumentNullException.ThrowIfNull(typeFinder);
        ArgumentNullException.ThrowIfNull(clock);
#else
        if (tx is null) throw new ArgumentNullException(nameof(tx));
        if (snapshotExtractor is null) throw new ArgumentNullException(nameof(snapshotExtractor));
        if (typeFinder is null) throw new ArgumentNullException(nameof(typeFinder));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
#endif
        _tx = tx;
        _snapshotExtractor = snapshotExtractor;
        _typeFinder = typeFinder;
        _clock = clock;
    }

    public SystemTypeSyncResult SyncTypeFromSource(
        Document sourceDoc,
        Document activeDoc,
        string typeName,
        string catalogItemId,
        string versionLabel,
        int sourceRevitVersion)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(sourceDoc);
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(catalogItemId);
#else
        if (sourceDoc is null) throw new ArgumentNullException(nameof(sourceDoc));
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (typeName is null) throw new ArgumentNullException(nameof(typeName));
        if (catalogItemId is null) throw new ArgumentNullException(nameof(catalogItemId));
#endif

        using var _scope = SmartConLogger.BeginScope(
            "SystemSync",
            ("Method", nameof(SyncTypeFromSource)),
            ("TypeName", typeName),
            ("CatalogItemId", catalogItemId));

        var sourceTypeId = _typeFinder.FindTypeByName(sourceDoc, typeName, null);
        if (sourceTypeId is null)
        {
            SmartConLogger.Warn(
                $"Type '{typeName}' not found in the source mini-project. " +
                "[Action: reimport the mini-project into the catalog — its type list is out of sync]");
            return new SystemTypeSyncResult(
                typeName, SystemTypeSyncStatus.NotFoundInSource, 0, 0,
                "Type not found in the source mini-project");
        }

        var sourceType = sourceDoc.GetElement(sourceTypeId) as ElementType;
        var categoryOrdinal = GetCategoryOrdinal(sourceType);
        var template = _snapshotExtractor.ExtractSingleSystemType(sourceDoc, sourceTypeId);

        SystemTypeSyncResult? result = null;
        var committed = _tx.RunInTransaction(activeDoc, $"SmartCon: Sync system type '{typeName}'", doc =>
        {
            var targetId = _typeFinder.FindTypeByName(doc, typeName, categoryOrdinal);
            var target = targetId is not null ? doc.GetElement(targetId) as ElementType : null;

            var status = SystemTypeSyncStatus.Updated;
            if (target is null)
            {
                var prototype = FindPrototypeType(doc, categoryOrdinal);
                if (prototype is null)
                {
                    SmartConLogger.Warn(
                        $"Type '{typeName}': no prototype type of category ordinal " +
                        $"{(categoryOrdinal?.ToString() ?? "<none>")} exists in the project; " +
                        "cannot create the type. " +
                        "[Action: create any type of this category in the project manually, then retry]");
                    result = new SystemTypeSyncResult(
                        typeName, SystemTypeSyncStatus.NoPrototypeType, 0, 0,
                        "No prototype type of the same category in the project");
                    return;
                }

                target = prototype.Duplicate(typeName);
                status = SystemTypeSyncStatus.Created;
            }

            var elementIdCache = new Dictionary<string, ElementId?>(StringComparer.Ordinal);
            var (written, skipped) = WriteParameters(doc, target, template, elementIdCache);

            RevitFamilyVersionStore.WriteEntityToElement(target, new FamilyVersion(
                SchemaVersion: FamilyVersion.CurrentSchemaVersion,
                CatalogItemId: catalogItemId,
                VersionLabel: versionLabel,
                LoadedAtUtc: _clock.UtcNow,
                SourceRevitVersion: sourceRevitVersion));

            result = new SystemTypeSyncResult(typeName, status, written, skipped);
        });

        if (!committed || result is null)
        {
            SmartConLogger.Warn(
                $"Type '{typeName}': sync transaction failed. " +
                "[Action: type skipped, batch continues; check the log for the transaction error]");
            return result ?? new SystemTypeSyncResult(
                typeName, SystemTypeSyncStatus.Failed, 0, 0,
                "Transaction failed");
        }

        SmartConLogger.Info(
            $"Type '{typeName}': {result.Status}, {result.ParametersWritten} parameters written, " +
            $"{result.ParametersSkipped} skipped.");
        return result;
    }

    private static int? GetCategoryOrdinal(ElementType? type)
    {
        try
        {
            var builtIn = Core.Compatibility.CategoryCompat.GetBuiltInCategory(type?.Category);
            if (builtIn == BuiltInCategory.INVALID) return null;
            return (int)builtIn;
        }
        catch
        {
            return null;
        }
    }

    private static ElementType? FindPrototypeType(Document doc, int? categoryOrdinal)
    {
        // Without a category we must not create: duplicating a type of a
        // foreign category would produce a wrong-category type. Creation is
        // only possible with a known category filter.
        if (!categoryOrdinal.HasValue) return null;

        try
        {
            using var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(ElementType))
                .OfCategoryId(Core.Compatibility.ElementIdCompat.Create(categoryOrdinal.Value));
            return collector.Cast<ElementType>().FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static (int Written, int Skipped) WriteParameters(
        Document doc,
        ElementType target,
        SystemTypeSnapshot template,
        Dictionary<string, ElementId?> elementIdCache)
    {
        var written = 0;
        var skipped = 0;
        var failCounter = new HotLoopCounter(sampleEvery: 16);

        foreach (var value in template.Values)
        {
            Parameter? param = null;
            try { param = target.LookupParameter(value.ParameterName); }
            catch { /* duplicate-name definitions — treated as missing */ }

            if (param is null || param.IsReadOnly)
            {
                if (failCounter.ShouldLog())
                {
                    SmartConLogger.Debug(
                        $"Parameter '{value.ParameterName}' skipped: " +
                        (param is null ? "not found on target" : "read-only"));
                }
                skipped++;
                continue;
            }

            if (!value.HasValue)
            {
                if (param.HasValue)
                {
                    try
                    {
                        param.ClearValue();
                        written++;
                    }
                    catch (Exception ex)
                    {
                        // Revit: ClearValue is only allowed on shared
                        // parameters. For plain string parameters an empty
                        // string is the canonical "no value" state.
                        var cleared = false;
                        if (param.StorageType == StorageType.String)
                        {
                            try { cleared = param.Set(string.Empty); }
                            catch { /* counted below */ }
                        }
                        if (cleared) written++;
                        else
                        {
                            SmartConLogger.Debug(
                                $"Parameter '{value.ParameterName}' clear failed: {ex.Message}");
                            skipped++;
                        }
                    }
                }
                continue;
            }

            var ok = false;
            try
            {
                ok = param.StorageType switch
                {
                    StorageType.Double => value.ValueNumber.HasValue && param.Set(value.ValueNumber.Value),
                    StorageType.Integer => value.ValueNumber.HasValue && param.Set((int)value.ValueNumber.Value),
                    StorageType.String => param.Set(value.ValueText ?? string.Empty),
                    StorageType.ElementId => TrySetElementId(doc, param, value, elementIdCache),
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                if (failCounter.ShouldLog())
                {
                    SmartConLogger.Debug(
                        $"Parameter '{value.ParameterName}' write failed: {ex.Message}");
                }
            }

            if (ok) written++;
            else skipped++;
        }

        return (written, skipped);
    }

    private static bool TrySetElementId(
        Document doc,
        Parameter param,
        SystemParameterValue value,
        Dictionary<string, ElementId?> cache)
    {
        var name = value.ResolvedElementName;
        if (string.IsNullOrEmpty(name)) return false;

        if (!cache.TryGetValue(name!, out var resolved))
        {
            resolved = ResolveElementByName(doc, name!);
            cache[name!] = resolved;
        }
        if (resolved is null) return false;

        try
        {
            return param.Set(resolved);
        }
        catch
        {
            return false;
        }
    }

    private static ElementId? ResolveElementByName(Document doc, string name)
    {
        using var materials = new FilteredElementCollector(doc).OfClass(typeof(Material));
        var material = materials.Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (material is not null) return material.Id;

        using var types = new FilteredElementCollector(doc).OfClass(typeof(ElementType));
        var type = types.Cast<ElementType>()
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        return type?.Id;
    }
}
