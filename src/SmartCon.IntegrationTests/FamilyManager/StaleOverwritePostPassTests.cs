using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #239 regression: «Обновить с перезаписью параметров» must land the
/// catalog version's parameter values on ALL loaded types, not only the
/// first one (per-symbol LoadFamilySymbol merge overwrites only the
/// requested symbol — probe-proven 2026-08-23). Full production chain:
/// real <see cref="StaleFamilyUpdater"/> + real <c>RevitFamilyLoadService</c>
/// (reload + catalog post-pass) + real post-verify (FHV10) — catalog-side
/// seams are fakes (<c>StubFileResolver</c>, stub repositories), the
/// "catalog values" are built from the v2 .rfa snapshot exactly as the
/// import pipeline persists them.
/// Pins:
///  1. overwrite=true → every loaded type gets the catalog values
///     (Score/Label/Flag), formula-driven Computed self-heals, update
///     succeeds (post-verify passes) and the marker is written;
///  2. overwrite=false → the values repository is never queried and the
///     project values stay untouched.
/// </summary>
public sealed class StaleOverwritePostPassTests : RevitApiTest
{
    private const string FamilyName = "PostPassScore";
    private const string ItemId = "postpass-item";
    private static readonly string[] TypeNames = ["T1", "T2", "T3"];

    private string? _template;
    private string? _tempDir;
    private string? _rfaPath;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the post-pass regression");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConPostPass_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _rfaPath = Path.Combine(_tempDir, FamilyName + ".rfa");
        _openDocs = new List<Document>();
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        if (_openDocs is not null)
        {
            foreach (var doc in _openDocs)
            {
                try
                {
                    if (doc.IsValidObject) doc.Close(false);
                }
                catch
                {
                }
            }
        }

        try
        {
            if (_tempDir is not null && Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    [Test]
    public async Task UpdateFamily_OverwriteTrue_AllTypesGetCatalogValues_AndPostVerifyPasses()
    {
        CreateFamilyRfa(_rfaPath!, [10.0, 20.0, 30.0], ["a", "b", "c"], [0, 1, 0], currentTypeName: "T2");
        var project = SeedProjectWithFamily(_rfaPath!);
        MutateFamilyRfa(_rfaPath!, [100.0, 200.0, 300.0], ["a2", "b2", "c2"], [1, 0, 1], currentTypeName: "T3");

        var (values, types) = BuildCatalogRowsFromFile(_rfaPath!);
        var writer = new RecordingVersionWriter();
        var updater = CreateUpdater(project, values, types, writer);

        var result = await updater.UpdateFamilyAsync(
            ItemId, overwriteParameterValues: true, fromVersionLabel: null, CancellationToken.None);

        var after = ReadAllValues(project);
        SmartConLogger.Info(
            $"#239 regression: update success={result.Success} markerWrites={writer.Calls.Count} " +
            $"values={Describe(after)}");

        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsTrue();
            await Assert.That(writer.Calls.Count).IsEqualTo(1);
            foreach (var (name, score, label, flag, computed) in new[]
            {
                ("T1", 100.0, "a2", 1, 200.0),
                ("T2", 200.0, "b2", 0, 400.0),
                ("T3", 300.0, "c2", 1, 600.0),
            })
            {
                var v = after[name];
                await Assert.That(v.Score).IsEqualTo(score).Because($"{name}.Score must come from the catalog v2");
                await Assert.That(v.Label).IsEqualTo(label).Because($"{name}.Label must come from the catalog v2");
                await Assert.That(v.Flag).IsEqualTo(flag).Because($"{name}.Flag must come from the catalog v2");
                await Assert.That(v.Computed).IsEqualTo(computed).Because($"{name}.Computed must self-heal from the formula");
                await Assert.That(v.Mat).IsEqualTo("ProbeMatB").Because($"{name}.Mat (ElementId) must resolve by name to the catalog v2 material");
            }
        }
    }

    [Test]
    public async Task UpdateFamily_OverwriteFalse_KeepsProjectValues_AndSkipsRepository()
    {
        CreateFamilyRfa(_rfaPath!, [10.0, 20.0, 30.0], ["a", "b", "c"], [0, 1, 0], currentTypeName: "T2");
        var project = SeedProjectWithFamily(_rfaPath!);
        MutateFamilyRfa(_rfaPath!, [100.0, 200.0, 300.0], ["a2", "b2", "c2"], [1, 0, 1], currentTypeName: "T3");

        var (values, types) = BuildCatalogRowsFromFile(_rfaPath!);
        var writer = new RecordingVersionWriter();
        var updater = CreateUpdater(project, values, types, writer);

        _ = await updater.UpdateFamilyAsync(
            ItemId, overwriteParameterValues: false, fromVersionLabel: null, CancellationToken.None);

        var after = ReadAllValues(project);
        SmartConLogger.Info(
            $"#239 keep-params: values={Describe(after)} repoCalls={values.GetValuesForItemCalls}");

        using (Assert.Multiple())
        {
            await Assert.That(values.GetValuesForItemCalls).IsEqualTo(0);
            foreach (var (name, score, label) in new[] { ("T1", 10.0, "a"), ("T2", 20.0, "b"), ("T3", 30.0, "c") })
            {
                var v = after[name];
                await Assert.That(v.Score).IsEqualTo(score).Because($"{name}.Score must stay at the project value");
                await Assert.That(v.Label).IsEqualTo(label).Because($"{name}.Label must stay at the project value");
            }
        }
    }

    private StaleFamilyUpdater CreateUpdater(
        Document project,
        StubAttributeValueRepository values,
        StubTypeRepository types,
        RecordingVersionWriter writer)
    {
        var context = new StubRevitContext(project);
        return new StaleFamilyUpdater(
            new RevitFamilyLoadService(context, new RevitTransactionService(context)),
            new StubFileResolver(_rfaPath!, "v2"),
            new RevitFamilyVersionStore(new RevitTransactionService(context)),
            new NullFamilyManagerDialogService(),
            new InlineAwaitableEvent(),
            context,
            writer,
            new StubClock(),
            nestedSharedRepository: null,
            catalog: null,
            typeRepository: types,
            systemSyncOrchestrator: null,
            dependencyRepository: null,
            snapshotExtractor: new RevitFamilySnapshotExtractor(),
            contentHasher: new FamilyContentHasher(),
            attributeValueRepository: values,
            familySearchService: null);
    }

    /// <summary>
    /// Builds the catalog-side rows (extracted values + type descriptors) from
    /// the v2 .rfa exactly as the import pipeline persists them: all parameter
    /// values of all types, with the attribute scope derived from the
    /// parameter definitions. Type ids follow the
    /// <c>"type-" + typeName</c> convention used by the stub repositories.
    /// </summary>
    private (StubAttributeValueRepository Values, StubTypeRepository Types) BuildCatalogRowsFromFile(string rfaPath)
    {
        var fileDoc = Application.OpenDocumentFile(rfaPath);
        _openDocs!.Add(fileDoc);

        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(fileDoc);
        var scopeByName = new Dictionary<string, AttributeScope>(StringComparer.Ordinal);
        foreach (var p in snapshot.Parameters)
        {
            if (!string.IsNullOrEmpty(p.Name))
                scopeByName[p.Name] = p.IsInstance ? AttributeScope.Instance : AttributeScope.Type;
        }

        var rows = new List<ExtractedAttributeValue>();
        var descriptors = new List<FamilyTypeDescriptor>();
        var sortOrder = 0;
        foreach (var t in snapshot.Types)
        {
            var typeId = "type-" + t.Name;
            descriptors.Add(new FamilyTypeDescriptor(typeId, ItemId, t.Name, sortOrder++, "version-id"));
            foreach (var v in t.Values)
            {
                rows.Add(new ExtractedAttributeValue(
                    Guid.NewGuid().ToString(), ItemId, "version-id", "file",
                    typeId, null, null,
                    v.ParameterName,
                    scopeByName.TryGetValue(v.ParameterName, out var scope) ? scope : (AttributeScope?)null,
                    v.StorageType,
                    v.ValueDisplay ?? v.ValueText,
                    null, v.ValueNumber, null,
                    v.HasValue ? AttributeValueStatus.Found : AttributeValueStatus.EmptyValue,
                    null, "run", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            }
        }

        return (new StubAttributeValueRepository(rows), new StubTypeRepository(descriptors));
    }

    private void CreateFamilyRfa(string path, double[] scores, string[] labels, int[] flags, string currentTypeName)
    {
        var doc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(doc, "seed family"))
        {
            tx.Start();
#if REVIT2022_OR_GREATER
            var score = doc.FamilyManager.AddParameter("Score", GroupTypeId.General, SpecTypeId.Number, false);
            var label = doc.FamilyManager.AddParameter("Label", GroupTypeId.General, SpecTypeId.String.Text, false);
            var flag = doc.FamilyManager.AddParameter("Flag", GroupTypeId.General, SpecTypeId.Boolean.YesNo, false);
            var computed = doc.FamilyManager.AddParameter("Computed", GroupTypeId.General, SpecTypeId.Number, false);
            var mat = doc.FamilyManager.AddParameter("Mat", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
#else
            var score = doc.FamilyManager.AddParameter("Score", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
            var label = doc.FamilyManager.AddParameter("Label", BuiltInParameterGroup.PG_GENERAL, ParameterType.Text, false);
            var flag = doc.FamilyManager.AddParameter("Flag", BuiltInParameterGroup.PG_GENERAL, ParameterType.YesNo, false);
            var computed = doc.FamilyManager.AddParameter("Computed", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
            var mat = doc.FamilyManager.AddParameter("Mat", BuiltInParameterGroup.PG_MATERIALS, ParameterType.Material, false);
#endif
            var matA = Material.Create(doc, "ProbeMatA");
            Material.Create(doc, "ProbeMatB");

            doc.FamilyManager.NewType(TypeNames[0]);
            doc.FamilyManager.SetFormula(computed, "Score * 2");

            for (var i = 0; i < TypeNames.Length; i++)
            {
                if (i > 0)
                    doc.FamilyManager.NewType(TypeNames[i]);
                doc.FamilyManager.Set(score, scores[i]);
                doc.FamilyManager.Set(label, labels[i]);
                doc.FamilyManager.Set(flag, flags[i]);
                doc.FamilyManager.Set(mat, matA);
            }

            SetCurrentType(doc, currentTypeName);
            tx.Commit();
        }

        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
    }

    private void MutateFamilyRfa(string path, double[] scores, string[] labels, int[] flags, string currentTypeName)
    {
        var doc = Application.OpenDocumentFile(path);
        try
        {
            using (var tx = new Transaction(doc, "mutate values"))
            {
                tx.Start();
                var score = doc.FamilyManager.get_Parameter("Score");
                var label = doc.FamilyManager.get_Parameter("Label");
                var flag = doc.FamilyManager.get_Parameter("Flag");
                var mat = doc.FamilyManager.get_Parameter("Mat");
                var matB = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .First(m => m.Name == "ProbeMatB").Id;
                for (var i = 0; i < TypeNames.Length; i++)
                {
                    SetCurrentType(doc, TypeNames[i]);
                    doc.FamilyManager.Set(score, scores[i]);
                    doc.FamilyManager.Set(label, labels[i]);
                    doc.FamilyManager.Set(flag, flags[i]);
                    doc.FamilyManager.Set(mat, matB);
                }

                SetCurrentType(doc, currentTypeName);
                tx.Commit();
            }

            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    private static void SetCurrentType(Document doc, string typeName)
    {
        foreach (FamilyType type in doc.FamilyManager.Types)
        {
            if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
            {
                doc.FamilyManager.CurrentType = type;
                return;
            }
        }

        throw new InvalidOperationException($"Family type '{typeName}' not found");
    }

    private Document SeedProjectWithFamily(string rfaPath)
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        _openDocs!.Add(doc);
        using (var tx = new Transaction(doc, "load family"))
        {
            tx.Start();
            if (!doc.LoadFamily(rfaPath, out _))
                throw new InvalidOperationException("LoadFamily(v1) returned false");
            tx.Commit();
        }

        var seed = ReadAllValues(doc);
        foreach (var name in TypeNames)
        {
            if (seed[name].Label is null)
                throw new InvalidOperationException($"Seed failed: symbol '{name}' has no Label value");
        }

        return doc;
    }

    private sealed record TypeValues(double? Score, string? Label, int? Flag, double? Computed, string? Mat);

    private static Dictionary<string, TypeValues> ReadAllValues(Document projectDoc)
    {
        var family = new FilteredElementCollector(projectDoc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, FamilyName, StringComparison.OrdinalIgnoreCase));

        var result = new Dictionary<string, TypeValues>(StringComparer.Ordinal);
        foreach (var id in family.GetFamilySymbolIds())
        {
            var symbol = (FamilySymbol)projectDoc.GetElement(id);
            var matId = symbol.LookupParameter("Mat")?.AsElementId();
            var matName = matId is not null && matId != ElementId.InvalidElementId
                ? projectDoc.GetElement(matId)?.Name
                : null;
            result[symbol.Name] = new TypeValues(
                symbol.LookupParameter("Score")?.AsDouble(),
                symbol.LookupParameter("Label")?.AsString(),
                symbol.LookupParameter("Flag")?.AsInteger(),
                symbol.LookupParameter("Computed")?.AsDouble(),
                matName);
        }

        return result;
    }

    private static string Describe(Dictionary<string, TypeValues> values)
    {
        return string.Join("; ", values
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp =>
            {
                var v = kvp.Value;
                return $"{kvp.Key}[Score={Fmt(v.Score)}, Label={v.Label}, Flag={v.Flag}, Computed={Fmt(v.Computed)}]";
            }));
    }

    private static string Fmt(double? value)
    {
        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }
}

/// <summary>
/// Stub of <see cref="IAttributeValueRepository"/> returning preset rows for
/// <c>GetValuesForItemAsync</c>; every other member throws.
/// </summary>
internal sealed class StubAttributeValueRepository : IAttributeValueRepository
{
    private readonly IReadOnlyList<ExtractedAttributeValue> _values;

    public StubAttributeValueRepository(IReadOnlyList<ExtractedAttributeValue> values)
    {
        _values = values;
    }

    public int GetValuesForItemCalls { get; private set; }

    public Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForItemAsync(
        string catalogItemId, string? versionId, CancellationToken ct = default)
    {
        GetValuesForItemCalls++;
        return Task.FromResult(_values);
    }

    public Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForTypeAsync(string typeId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> GetDistinctValueTextsAsync(string attributeId, string attributeName, int limit = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForRunAsync(string runId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task SaveValuesAsync(IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task ReplaceSnapshotAsync(string catalogItemId, string? versionId, string runId, IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<int> DeleteValuesForRunAsync(string runId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<int> GetFoundCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<int> GetMissingCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Stub of <see cref="IFamilyTypeRepository"/> returning preset type
/// descriptors for <c>GetTypesForItemVersionAsync</c>; every other member throws.
/// </summary>
internal sealed class StubTypeRepository : IFamilyTypeRepository
{
    private readonly IReadOnlyList<FamilyTypeDescriptor> _types;

    public StubTypeRepository(IReadOnlyList<FamilyTypeDescriptor> types)
    {
        _types = types;
    }

    public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(
        string catalogItemId, string? versionId, CancellationToken ct = default)
    {
        return Task.FromResult(_types);
    }

    public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
        string catalogItemId, string? versionId, string? fileId, string runId,
        IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default)
        => throw new NotSupportedException();
}
