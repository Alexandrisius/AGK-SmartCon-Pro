using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IFamilyVersionStore"/>. Reads/writes the
/// <c>SmartCon.FamilyVersion.v1</c> ExtensibleStorage marker from
/// <c>Family</c> elements (ADR-030).
/// </summary>
/// <remarks>
/// <para>Threading: every method must be called from the Revit main thread (I-01).
/// Project-document writes go through <see cref="ITransactionService"/> (I-03);
/// <c>.rfa</c> writes use a direct <c>new Transaction(familyDoc, ...)</c> because
/// family documents are not the active document (I-03b exception).</para>
/// <para>Revit API quirks handled here:
/// <list type="bullet">
///   <item><description><c>Entity</c> is <see cref="IDisposable"/> — must be wrapped
///     in <c>using</c> to avoid leaking native handles.</description></item>
///   <item><description>Opening an <c>.rfa</c> via <c>app.OpenDocumentFile</c>
///     leaves a reference even after <c>Close</c> — known bug REVIT-237190.
///     We <see cref="Marshal.ReleaseComObject"/> it in the <c>finally</c> block.</description></item>
///   <item><description>Corrupted entity data is treated as missing — no exception
///     propagates to the caller (logged via <see cref="SmartConLogger"/>).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class RevitFamilyVersionStore : IFamilyVersionStore
{
    private readonly ITransactionService _tx;
    private readonly IRevitContext _revitContext;

    public RevitFamilyVersionStore(ITransactionService tx, IRevitContext revitContext)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(revitContext);
#else
        if (tx is null) throw new ArgumentNullException(nameof(tx));
        if (revitContext is null) throw new ArgumentNullException(nameof(revitContext));
#endif
        _tx = tx;
        _revitContext = revitContext;
    }

    public FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId)
    {
        if (doc is null) return null;
        if (familyId is null) return null;

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

    public Task<FamilyVersion?> ReadFromRfaFileAsync(string rfaFilePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rfaFilePath)) return Task.FromResult<FamilyVersion?>(null);
        if (!File.Exists(rfaFilePath)) return Task.FromResult<FamilyVersion?>(null);

        Document? familyDoc = null;
        try
        {
            var app = _revitContext.GetDocument().Application;
            familyDoc = app.OpenDocumentFile(rfaFilePath);
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
                return Task.FromResult<FamilyVersion?>(null);

            var owner = familyDoc.OwnerFamily;
            if (owner is null) return Task.FromResult<FamilyVersion?>(null);

            var schema = FamilyVersionSchema.GetOrCreate();
            using var entity = owner.GetEntity(schema);
            if (!entity.IsValid()) return Task.FromResult<FamilyVersion?>(null);

            return Task.FromResult<FamilyVersion?>(ReadEntity(entity));
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(ReadFromRfaFileAsync)),
                ("FileName", Path.GetFileName(rfaFilePath)));
            SmartConLogger.Warn(
                $"ReadFromRfaFileAsync[{Path.GetFileName(rfaFilePath)}]: failed: {ex.Message}. " +
                "[Action: rfa skipped, batch continues]");
            return Task.FromResult<FamilyVersion?>(null);
        }
        finally
        {
            if (familyDoc != null)
            {
                try { familyDoc.Close(false); } catch { }
                try { Marshal.ReleaseComObject(familyDoc); } catch { }
            }
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

        _tx.RunInTransaction(doc, "SmartCon: Write FamilyVersion", txDoc =>
        {
            var family = txDoc.GetElement(familyId) as Autodesk.Revit.DB.Family;
            if (family is null) return;

            var schema = FamilyVersionSchema.GetOrCreate();
            using var entity = new Entity(schema);
            entity.Set(FamilyVersionSchema.FieldSchemaVersion, FamilyVersion.CurrentSchemaVersion);
            entity.Set(FamilyVersionSchema.FieldCatalogItemId, version.CatalogItemId ?? string.Empty);
            entity.Set(FamilyVersionSchema.FieldVersionLabel, version.VersionLabel ?? string.Empty);
            entity.Set(FamilyVersionSchema.FieldLoadedAtUtc, version.LoadedAtUtc.ToString("o"));
            entity.Set(FamilyVersionSchema.FieldSourceRevitVersion, version.SourceRevitVersion);
            family.SetEntity(entity);
        });
    }

    public Task WriteToRfaFileAsync(string rfaFilePath, FamilyVersion version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rfaFilePath)) return Task.CompletedTask;
        if (!File.Exists(rfaFilePath)) return Task.CompletedTask;
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(version);
#else
        if (version is null) throw new ArgumentNullException(nameof(version));
#endif

        Document? familyDoc = null;
        try
        {
            var app = _revitContext.GetDocument().Application;
            familyDoc = app.OpenDocumentFile(rfaFilePath);
            if (familyDoc is null || !familyDoc.IsFamilyDocument)
            {
                using var _scope = SmartConLogger.BeginScope(
                    "StaleDetection",
                    ("Method", nameof(WriteToRfaFileAsync)),
                    ("FileName", Path.GetFileName(rfaFilePath)));
                SmartConLogger.Warn(
                    $"{Path.GetFileName(rfaFilePath)}: not a family document. [Action: skip]");
                return Task.CompletedTask;
            }

            using (var familyTx = new Transaction(familyDoc, "SmartCon: Write FamilyVersion"))
            {
                familyTx.Start();
                var owner = familyDoc.OwnerFamily;
                if (owner is null)
                {
                    familyTx.RollBack();
                    return Task.CompletedTask;
                }

                var schema = FamilyVersionSchema.GetOrCreate();
                using var entity = new Entity(schema);
                entity.Set(FamilyVersionSchema.FieldSchemaVersion, FamilyVersion.CurrentSchemaVersion);
                entity.Set(FamilyVersionSchema.FieldCatalogItemId, version.CatalogItemId ?? string.Empty);
                entity.Set(FamilyVersionSchema.FieldVersionLabel, version.VersionLabel ?? string.Empty);
                entity.Set(FamilyVersionSchema.FieldLoadedAtUtc, version.LoadedAtUtc.ToString("o"));
                entity.Set(FamilyVersionSchema.FieldSourceRevitVersion, version.SourceRevitVersion);
                owner.SetEntity(entity);
                familyTx.Commit();
            }

            familyDoc.Save();
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(WriteToRfaFileAsync)),
                ("FileName", Path.GetFileName(rfaFilePath)));
            SmartConLogger.Warn(
                $"WriteToRfaFileAsync[{Path.GetFileName(rfaFilePath)}]: failed: {ex.Message}. " +
                "[Action: rfa write skipped, batch continues]");
        }
        finally
        {
            if (familyDoc != null)
            {
                try { familyDoc.Close(false); } catch { }
                try { Marshal.ReleaseComObject(familyDoc); } catch { }
            }
        }
        return Task.CompletedTask;
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

        foreach (var id in familyIds)
        {
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
        }
        return result;
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
        catch
        {
            return null;
        }
    }
}
