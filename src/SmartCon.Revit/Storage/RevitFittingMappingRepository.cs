using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Storage;

namespace SmartCon.Revit.Storage;

/// <summary>
/// Persists fitting mapping data per Revit project using ExtensibleStorage (ADR-012).
/// The payload is stored as a JSON string inside a <see cref="DataStorage"/> element
/// tagged with <see cref="FittingMappingSchema"/>. Import/Export between projects is
/// performed manually through the Settings window.
/// </summary>
/// <remarks>
/// Lifecycle: singleton; the active <see cref="Document"/> is fetched through
/// <see cref="IRevitContext.GetDocument"/> on every call so switching projects is
/// handled automatically. Read operations never open a transaction (no "Save Mapping"
/// Undo entry appears when simply opening a project). Writes go through
/// <see cref="ITransactionService"/>, which satisfies invariants I-03 / I-07.
/// </remarks>
public sealed class RevitFittingMappingRepository : IFittingMappingRepository
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _transactionService;

    public RevitFittingMappingRepository(
        IRevitContext revitContext,
        ITransactionService transactionService)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    }

    // ── IFittingMappingRepository ─────────────────────────────────────────

    public IReadOnlyList<ConnectorTypeDefinition> GetConnectorTypes()
        => LoadPayload().ConnectorTypes;

    public IReadOnlyList<FittingMappingRule> GetMappingRules()
        => LoadPayload().MappingRules;

    public void SaveConnectorTypes(IReadOnlyList<ConnectorTypeDefinition> types)
    {
#if NETFRAMEWORK
        if (types is null) throw new ArgumentNullException(nameof(types));
#else
        ArgumentNullException.ThrowIfNull(types);
#endif
        var existing = LoadPayload();
        SavePayload(new MappingPayload(
            FittingMappingJsonSerializer.CurrentVersion,
            types,
            existing.MappingRules));
    }

    public void SaveMappingRules(IReadOnlyList<FittingMappingRule> rules)
    {
#if NETFRAMEWORK
        if (rules is null) throw new ArgumentNullException(nameof(rules));
#else
        ArgumentNullException.ThrowIfNull(rules);
#endif
        var existing = LoadPayload();
        SavePayload(new MappingPayload(
            FittingMappingJsonSerializer.CurrentVersion,
            existing.ConnectorTypes,
            rules));
    }

    /// <summary>
    /// Returns a diagnostic description of the current storage (not a file system path).
    /// Format: <c>ExtensibleStorage:{docTitle}@SchemaV{n}</c>.
    /// </summary>
    public string GetStoragePath()
    {
        var doc = TryGetDocument();
        if (doc is null) return "Document not available";

        return $"ExtensibleStorage:{doc.Title}@SchemaV{FittingMappingJsonSerializer.CurrentVersion}";
    }

    // ── Payload helpers (read-only, no transactions) ──────────────────────

    private MappingPayload LoadPayload()
    {
        var doc = TryGetDocument();
        if (doc is null) return MappingPayload.Empty;

        try
        {
            var schema = FittingMappingSchema.GetOrCreate();
            var storage = FindDataStorage(doc, schema);
            if (storage is null) return MappingPayload.Empty;

            using var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return MappingPayload.Empty;

            var payloadJson = entity.Get<string>(FittingMappingSchema.FieldPayload);
            return FittingMappingJsonSerializer.Deserialize(payloadJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            SmartConLogger.Error($"[Mapping] Corrupted payload: {ex.Message}. Returning empty payload.");
            return MappingPayload.Empty;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Mapping] LoadPayload failed: {ex.GetType().Name}: {ex.Message}");
            return MappingPayload.Empty;
        }
    }

    private void SavePayload(MappingPayload payload)
    {
        var doc = TryGetDocument();
        if (doc is null)
        {
            SmartConLogger.Warn("[Mapping] SavePayload skipped: active document is not available.");
            return;
        }

        var json = FittingMappingJsonSerializer.Serialize(payload);

        _transactionService.RunInTransaction("SmartCon: Save Mapping", txDoc =>
        {
            var schema = FittingMappingSchema.GetOrCreate();
            var storage = FindDataStorage(txDoc, schema) ?? CreateDataStorage(txDoc);

            using var entity = new Entity(schema);
            entity.Set(FittingMappingSchema.FieldSchemaVersion, payload.SchemaVersion);
            entity.Set(FittingMappingSchema.FieldPayload, json);

            storage.SetEntity(entity);
        });
    }

    // ── DataStorage lookup / create ───────────────────────────────────────

    private static DataStorage? FindDataStorage(Document doc, Schema schema)
    {
        using var collector = new FilteredElementCollector(doc);
        var matches = collector
            .OfClass(typeof(DataStorage))
            .Cast<DataStorage>()
            .Where(ds => HasValidEntity(ds, schema))
            .ToList();

        if (matches.Count == 0) return null;
        if (matches.Count > 1)
        {
            SmartConLogger.Warn(
                $"[Mapping] Found {matches.Count} DataStorage elements with SmartCon schema. Using the first one (ids: " +
                string.Join(", ", matches.Select(m => m.Id.GetValue())) + ").");
        }

        return matches[0];
    }

    private static bool HasValidEntity(DataStorage storage, Schema schema)
    {
        using var entity = storage.GetEntity(schema);
        return entity.IsValid();
    }

    private static DataStorage CreateDataStorage(Document doc)
    {
        var storage = DataStorage.Create(doc);
        storage.Name = FittingMappingSchema.DataStorageName;
        return storage;
    }

    private Document? TryGetDocument()
    {
        try
        {
            return _revitContext.GetDocument();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
