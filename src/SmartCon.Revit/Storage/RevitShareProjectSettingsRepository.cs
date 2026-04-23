using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Storage;

namespace SmartCon.Revit.Storage;

public sealed class RevitShareProjectSettingsRepository : IShareProjectSettingsRepository
{
    private readonly ITransactionService _transactionService;

    public RevitShareProjectSettingsRepository(ITransactionService transactionService)
    {
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    }

    public ShareProjectSettings Load(Document doc)
    {
#if NETFRAMEWORK
        if (doc is null) throw new ArgumentNullException(nameof(doc));
#else
        ArgumentNullException.ThrowIfNull(doc);
#endif
        try
        {
            var schema = ProjectManagementSchema.GetOrCreate();
            var storage = FindDataStorage(doc, schema);
            if (storage is null) return ShareProjectSettings.Empty;

            using var entity = storage.GetEntity(schema);
            if (!entity.IsValid()) return ShareProjectSettings.Empty;

            var payloadJson = entity.Get<string>(ProjectManagementSchema.FieldPayload);
            return ShareSettingsJsonSerializer.Deserialize(payloadJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            SmartConLogger.Error($"[PM] Corrupted payload: {ex.Message}. Returning empty settings.");
            return ShareProjectSettings.Empty;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[PM] Load failed: {ex.GetType().Name}: {ex.Message}");
            return ShareProjectSettings.Empty;
        }
    }

    public void Save(Document doc, ShareProjectSettings settings)
    {
#if NETFRAMEWORK
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
#else
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(settings);
#endif
        var json = ShareSettingsJsonSerializer.Serialize(settings);

        _transactionService.RunInTransaction("SmartCon: Save ShareSettings", txDoc =>
        {
            var schema = ProjectManagementSchema.GetOrCreate();
            var storage = FindDataStorage(txDoc, schema) ?? CreateDataStorage(txDoc);

            using var entity = new Entity(schema);
            entity.Set(ProjectManagementSchema.FieldSchemaVersion, ShareSettingsJsonSerializer.CurrentVersion);
            entity.Set(ProjectManagementSchema.FieldPayload, json);

            storage.SetEntity(entity);
        });
    }

    public string ExportToJson(ShareProjectSettings settings)
    {
#if NETFRAMEWORK
        if (settings is null) throw new ArgumentNullException(nameof(settings));
#else
        ArgumentNullException.ThrowIfNull(settings);
#endif
        return ShareSettingsJsonSerializer.Serialize(settings);
    }

    public ShareProjectSettings ImportFromJson(string json)
    {
        return ShareSettingsJsonSerializer.Deserialize(json);
    }

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
                $"[PM] Found {matches.Count} DataStorage elements with PM schema. Using the first one (ids: " +
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
        storage.Name = ProjectManagementSchema.DataStorageName;
        return storage;
    }
}
