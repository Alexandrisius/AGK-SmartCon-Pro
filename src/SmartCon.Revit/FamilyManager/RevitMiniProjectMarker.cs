using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IMiniProjectMarker"/> (Issue #188).
/// The marker lives on a <c>DataStorage</c> element
/// (<see cref="MiniProjectSchema.DataStorageName"/>) so it travels with the
/// staged file across Save/close/reopen.
/// </summary>
public sealed class RevitMiniProjectMarker : IMiniProjectMarker
{
    private readonly ITransactionService _transactionService;
    private readonly IClock _clock;

    public RevitMiniProjectMarker(ITransactionService transactionService, IClock clock)
    {
        _transactionService = transactionService;
        _clock = clock;
    }

    public void MarkAsMiniProject(Document doc, string? catalogItemId)
    {
        _transactionService.RunInTransaction(doc, "Mark SmartCon mini-project", d =>
        {
            var schema = MiniProjectSchema.GetOrCreate();
            var storage = FindDataStorage(d, schema) ?? CreateDataStorage(d);
            var entity = new Entity(schema);
            entity.Set(MiniProjectSchema.FieldSchemaVersion, MiniProjectSchema.CurrentSchemaVersion);
            entity.Set(MiniProjectSchema.FieldCatalogItemId, catalogItemId ?? string.Empty);
            entity.Set(MiniProjectSchema.FieldMarkedAtUtc, _clock.UtcNow.ToString("o"));
            storage.SetEntity(entity);
        });
    }

    public bool IsMiniProject(Document doc)
    {
        var schema = Schema.Lookup(MiniProjectSchema.SchemaGuid);
        if (schema is null) return false;
        return FindDataStorage(doc, schema) is not null;
    }

    public string? ReadCatalogItemId(Document doc)
    {
        var schema = Schema.Lookup(MiniProjectSchema.SchemaGuid);
        if (schema is null) return null;
        var storage = FindDataStorage(doc, schema);
        if (storage is null) return null;
        using var entity = storage.GetEntity(schema);
        var id = entity.Get<string>(MiniProjectSchema.FieldCatalogItemId);
        return string.IsNullOrEmpty(id) ? null : id;
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
            using var _scope = SmartConLogger.BeginScope("MiniProject", ("Method", nameof(FindDataStorage)));
            SmartConLogger.Warn(
                $"Found {matches.Count} DataStorage elements with the mini-project schema. Using the first one (ids: " +
                string.Join(", ", matches.Select(m => m.Id.GetValue())) + "). " +
                "[Action: удалите лишние элементы SmartCon.MiniProject через RevitLookup — маркер дублирован]");
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
        storage.Name = MiniProjectSchema.DataStorageName;
        return storage;
    }
}
