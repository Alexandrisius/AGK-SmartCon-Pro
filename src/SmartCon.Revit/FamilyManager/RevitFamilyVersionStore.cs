using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IFamilyVersionStore"/>. Reads/writes the
/// <c>SmartCon_FamilyVersion_v1</c> ExtensibleStorage marker from <c>Family</c>
/// elements in the active project (ADR-030).
/// </summary>
/// <remarks>
/// <para>Threading: every method must be called from the Revit main thread (I-01).
/// Project-document writes go through <see cref="ITransactionService"/> (I-03).</para>
/// <para>Revit API quirks handled here:
/// <list type="bullet">
///   <item><description><c>Entity</c> is <see cref="IDisposable"/> — must be wrapped
///     in <c>using</c> to avoid leaking native handles.</description></item>
///   <item><description>Corrupted entity data is treated as missing — no exception
///     propagates to the caller (logged via <see cref="SmartConLogger"/>).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class RevitFamilyVersionStore : IFamilyVersionStore, ISystemTypeVersionStore
{
    private readonly ITransactionService _tx;

    public RevitFamilyVersionStore(ITransactionService tx)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tx);
#else
        if (tx is null) throw new ArgumentNullException(nameof(tx));
#endif
        _tx = tx;
    }

    public FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId)
    {
        if (doc is null) return null;
        if (familyId is null) return null;
        if (familyId == ElementId.InvalidElementId) return null;

        try
        {
            var family = doc.GetElement(familyId) as Autodesk.Revit.DB.Family;
            if (family is null) return null;

            var schema = FamilyVersionSchema.GetOrCreate();
            using var entity = family.GetEntity(schema);
            if (!entity.IsValid()) return null;

            return ReadEntity(entity);
        }
        catch (Exception ex)
        {
#if NET8_0_OR_GREATER
            var idValue = familyId.Value;
#else
#pragma warning disable CS0618 // IntegerValue is deprecated in Revit 2024; removed in 2025. Use Value when available.
            var idValue = familyId.IntegerValue;
#pragma warning restore CS0618
#endif
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(ReadFromLoadedFamily)),
                ("FamilyId", idValue));
            SmartConLogger.Warn(
                $"ReadFromLoadedFamily[{idValue}]: failed: {ex.Message}. " +
                "[Action: family skipped, batch continues]");
            return null;
        }
    }

    public void WriteToLoadedFamily(Document doc, ElementId familyId, FamilyVersion version)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(familyId);
        ArgumentNullException.ThrowIfNull(version);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (familyId is null) throw new ArgumentNullException(nameof(familyId));
        if (version is null) throw new ArgumentNullException(nameof(version));
#endif

        if (familyId == ElementId.InvalidElementId)
            throw new ArgumentException("Cannot write FamilyVersion marker to an invalid ElementId", nameof(familyId));

        _tx.RunInTransaction(doc, "SmartCon: Write FamilyVersion", txDoc =>
        {
            var family = txDoc.GetElement(familyId) as Autodesk.Revit.DB.Family;
            if (family is null) return;

            WriteEntityToElement(family, version);
        });
    }

    public IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromDocument(
        Document doc, IEnumerable<ElementId> familyIds)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(familyIds);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (familyIds is null) throw new ArgumentNullException(nameof(familyIds));
#endif

        var schema = FamilyVersionSchema.GetOrCreate();
        var result = new Dictionary<ElementId, FamilyVersion?>();
        var counter = new HotLoopCounter(sampleEvery: 32);
        var count = 0;
        foreach (var id in familyIds)
        {
            if (id is null) continue;
            if (id == ElementId.InvalidElementId) continue;
            try
            {
                var family = doc.GetElement(id) as Autodesk.Revit.DB.Family;
                if (family is null)
                {
                    result[id] = null;
                    continue;
                }

                using var entity = family.GetEntity(schema);
                if (!entity.IsValid())
                {
                    result[id] = null;
                    continue;
                }

                result[id] = ReadEntity(entity);
            }
            catch (Exception ex)
            {
                if (counter.ShouldLog())
                {
#if NET8_0_OR_GREATER
                    var idValue = id.Value;
#else
#pragma warning disable CS0618 // IntegerValue is deprecated in Revit 2024; removed in 2025. Use Value when available.
                    var idValue = id.IntegerValue;
#pragma warning restore CS0618
#endif
                    using var _scope = SmartConLogger.BeginScope(
                        "StaleDetection",
                        ("Method", nameof(ReadManyFromDocument)));
                    SmartConLogger.Warn(
                        $"ReadManyFromDocument[{idValue}]: {ex.Message}. " +
                        $"[Action: family skipped, batch continues (processed {counter.Count})]");
                }
                result[id] = null;
            }
            count++;
        }
        // Final progress log so a large batch (10k+ IDs) does not stay
        // silent in the log when no exception was thrown. Counter only logs
        // on sampled values; we want a final line for the happy path.
        if (count > 32)
        {
            SmartConLogger.Debug(
                $"ReadManyFromDocument: processed {count} families");
        }
        return result;
    }

    public FamilyVersion? ReadFromType(Document doc, ElementId typeId)
    {
        if (doc is null) return null;
        if (typeId is null) return null;
        if (typeId == ElementId.InvalidElementId) return null;

        try
        {
            var type = doc.GetElement(typeId) as ElementType;
            if (type is null) return null;

            var schema = FamilyVersionSchema.GetOrCreate();
            using var entity = type.GetEntity(schema);
            if (!entity.IsValid()) return null;

            return ReadEntity(entity);
        }
        catch (Exception ex)
        {
#if NET8_0_OR_GREATER
            var idValue = typeId.Value;
#else
#pragma warning disable CS0618 // IntegerValue is deprecated in Revit 2024; removed in 2025. Use Value when available.
            var idValue = typeId.IntegerValue;
#pragma warning restore CS0618
#endif
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(ReadFromType)),
                ("TypeId", idValue));
            SmartConLogger.Warn(
                $"ReadFromType[{idValue}]: failed: {ex.Message}. " +
                "[Action: type skipped, batch continues]");
            return null;
        }
    }

    public void WriteToType(Document doc, ElementId typeId, FamilyVersion version)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(typeId);
        ArgumentNullException.ThrowIfNull(version);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (typeId is null) throw new ArgumentNullException(nameof(typeId));
        if (version is null) throw new ArgumentNullException(nameof(version));
#endif

        if (typeId == ElementId.InvalidElementId)
            throw new ArgumentException("Cannot write FamilyVersion marker to an invalid ElementId", nameof(typeId));

        _tx.RunInTransaction(doc, "SmartCon: Write FamilyVersion (system type)", txDoc =>
        {
            var type = txDoc.GetElement(typeId) as ElementType;
            if (type is null) return;

            WriteEntityToElement(type, version);
        });
    }

    public IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromTypes(
        Document doc, IEnumerable<ElementId> typeIds)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(typeIds);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (typeIds is null) throw new ArgumentNullException(nameof(typeIds));
#endif

        var schema = FamilyVersionSchema.GetOrCreate();
        var result = new Dictionary<ElementId, FamilyVersion?>();
        var counter = new HotLoopCounter(sampleEvery: 32);
        foreach (var id in typeIds)
        {
            if (id is null) continue;
            if (id == ElementId.InvalidElementId) continue;
            try
            {
                var type = doc.GetElement(id) as ElementType;
                if (type is null)
                {
                    result[id] = null;
                    continue;
                }

                using var entity = type.GetEntity(schema);
                if (!entity.IsValid())
                {
                    result[id] = null;
                    continue;
                }

                result[id] = ReadEntity(entity);
            }
            catch (Exception ex)
            {
                if (counter.ShouldLog())
                {
#if NET8_0_OR_GREATER
                    var idValue = id.Value;
#else
#pragma warning disable CS0618 // IntegerValue is deprecated in Revit 2024; removed in 2025. Use Value when available.
                    var idValue = id.IntegerValue;
#pragma warning restore CS0618
#endif
                    using var _scope = SmartConLogger.BeginScope(
                        "StaleDetection",
                        ("Method", nameof(ReadManyFromTypes)));
                    SmartConLogger.Warn(
                        $"ReadManyFromTypes[{idValue}]: {ex.Message}. " +
                        $"[Action: type skipped, batch continues (processed {counter.Count})]");
                }
                result[id] = null;
            }
        }
        return result;
    }

    /// <summary>
    /// Write the marker entity directly onto an element. Intended for callers
    /// that already hold an open transaction (e.g. the system-type
    /// synchronizer writes the marker inside its single sync transaction so
    /// the whole operation stays one Undo step).
    /// </summary>
    internal static void WriteEntityToElement(Element element, FamilyVersion version)
    {
        var schema = FamilyVersionSchema.GetOrCreate();
        using var entity = new Entity(schema);
        entity.Set(FamilyVersionSchema.FieldSchemaVersion, FamilyVersion.CurrentSchemaVersion);
        entity.Set(FamilyVersionSchema.FieldCatalogItemId, version.CatalogItemId);
        entity.Set(FamilyVersionSchema.FieldVersionLabel, version.VersionLabel);
        entity.Set(FamilyVersionSchema.FieldLoadedAtUtc, version.LoadedAtUtc.ToString("o"));
        entity.Set(FamilyVersionSchema.FieldSourceRevitVersion, version.SourceRevitVersion);
        element.SetEntity(entity);
    }

    private static FamilyVersion? ReadEntity(Entity entity)
    {
        try
        {
            var schemaVersion = entity.Get<int>(FamilyVersionSchema.FieldSchemaVersion);
            var catalogItemId = entity.Get<string>(FamilyVersionSchema.FieldCatalogItemId) ?? string.Empty;
            var versionLabel = entity.Get<string>(FamilyVersionSchema.FieldVersionLabel) ?? string.Empty;
            var loadedAtText = entity.Get<string>(FamilyVersionSchema.FieldLoadedAtUtc) ?? string.Empty;
            var sourceRevit = entity.Get<int>(FamilyVersionSchema.FieldSourceRevitVersion);

            var loadedAt = string.IsNullOrEmpty(loadedAtText)
                ? DateTimeOffset.MinValue
                : DateTimeOffset.Parse(loadedAtText);

            return new FamilyVersion(schemaVersion, catalogItemId, versionLabel, loadedAt, sourceRevit);
        }
        catch (Exception ex)
        {
            // Logged so a future schema-rewrite bug does not silently lose
            // data; previously the catch was a bare 'catch' which made the
            // bad data impossible to diagnose. We still return null so the
            // caller treats the family as having no marker.
            SmartConLogger.Warn(
                $"ReadEntity: failed to deserialize FamilyVersion: " +
                $"{ex.GetType().Name}: {ex.Message}. " +
                "[Action: ES marker is corrupt; the family will be treated as " +
                "fresh-loaded on the next stale check]");
            return null;
        }
    }
}
